#!/usr/bin/env bash
# Starts the published build (out/) on a throwaway port, runs the real-browser tests (src/frontend/tankstat-react/e2e) against it, always cleans up.
# Playwright runs natively with E2E_NATIVE=1 (CI, which installs Chromium), otherwise in the Playwright image of the installed version
# (this machine's Linux is not one Playwright supports). Arguments go to `playwright test`: scripts/test-e2e.sh --project phone
# Besides it, six instances that wait for their setup (two of them set up first, so their setup reopens), on the next ports, for the
# setup's tests (E2E_SETUP).
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"
if [[ ! -f out/Tankstat.Api.dll ]]; then
  echo "No build in out/: run 'mise run build' first (mise run test:e2e does)." >&2
  exit 1
fi

port="${E2E_PORT:-5097}"
export E2E_URL="http://127.0.0.1:${port}"
# The app and the six waiting servers take seven ports from it: one already answering (another run, another session's) would answer
# for them, and the tests would fail on its data (#236).
for p in $(seq "$port" $((port + 6))); do
  if curl -s -o /dev/null --max-time 2 "http://127.0.0.1:${p}"; then
    echo "Something already answers on port ${p} (this run takes ${port}-$((port + 6))): stop it, or choose another start with E2E_PORT." >&2
    exit 1
  fi
done

# Nothing of the developer's own setup: the mode, the database, the folders, the schedule, the photo reading, the telemetry, the
# defaults the pages show (a date format), the logging the start is read from. .NET reads the variables' names in any case.
shopt -s nocasematch
for name in $(compgen -e); do
  case "$name" in
    Auth__* | Database__* | Storage__* | Settings__* | Backups__* | Recognition__* | DataProtection__* | Setup__* | Smtp__* | \
      Defaults__* | Notifications__* | Sync__* | Logging__* | Kestrel__* | ASPNETCORE_* | OTEL_* | Telemetry__*) unset "$name" ;;
  esac
done
shopt -u nocasematch

files="$(mktemp -d -t tankstat-e2e-XXXXXX)"
# The settings the shared server takes from its environment besides the machine's own: the OIDC values, an administrator list and a backup
# time on no day (Backups__Days=None, nothing scheduled) make the read-only rows of the Server settings page. The restore servers below set
# them too, as a move with the same compose file does: a backup records them, and a restore where one has no value is refused (#140).
instance_env=(
  Auth__Oidc__Authority=https://auth.example-identity.test/application/o/tankstat-production/
  Auth__Oidc__ClientId=1qViVYDsILFCoXa8Rk2mT5pWn9QeZ4uB7yHcJ3dE Auth__Oidc__ClientSecret=made-up-secret
  Auth__Oidc__Scopes__0=groups Auth__AdminEmails__0=first.administrator@example.com
  Backups__Every=03:00 Backups__Days=None
)
app() {
  # From out/: the content root (wwwroot, the web app) is the working directory, not the dll's folder.
  cd out
  ASPNETCORE_ENVIRONMENT=Production \
    Database__Provider=Sqlite Database__ConnectionString="Data Source=${files}/tankstat.db" \
    Storage__Path="${files}/uploads" Storage__PhotosPath="${files}/photos" Backups__Path="${files}/backups" \
    DataProtection__KeysPath="${files}/keys" Settings__Path="${files}/settings.json" \
    Auth__Mode=Standalone \
    exec env "${instance_env[@]}" dotnet Tankstat.Api.dll --urls "$E2E_URL"
}
# A new Standalone instance waits for its setup, the only way to its first administrator (#149): finished below the unattended way, as
# root@example.com, whom the tests sign in as (src/frontend/tankstat-react/e2e/support/app.ts).
app > "${files}/app.log" 2>&1 &
pids=($!)
trap 'kill "${pids[@]}" 2>/dev/null || true; wait "${pids[@]}" 2>/dev/null || true; rm -rf "$files"' EXIT

# Waits for a server's API: false when it stopped or never answered.
answers() {
  for _ in $(seq 1 150); do
    if curl -sf -H 'Content-Type: application/json' -d '{"query":"{ __typename }"}' "$1/graphql" >/dev/null; then return 0; fi
    kill -0 "$2" 2>/dev/null || return 1
    sleep 0.2
  done
  return 1
}

# Waits for the one-time setup code a server's start logs (its log $1, its process $2) and prints it: false when the server stopped
# first or logged none. The start logs it before its API answers; a reopened server's first start (below) logs none.
setup_code() {
  local code
  for _ in $(seq 1 300); do
    code="$(grep -oE 'setup code [A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}' "$1" | tail -n 1 | cut -d' ' -f3 || true)"
    if [[ -n "$code" ]]; then echo "$code"; return 0; fi
    kill -0 "$2" 2>/dev/null || return 1
    sleep 0.2
  done
  return 1
}

# The servers the setup's tests take over (src/frontend/tankstat-react/e2e/setup.e2e.ts), one per test and size: each is an instance waiting for its setup,
# which its test finishes or restores a backup into, so none is shared. "fresh" runs the shipped sign-in off (the wizard chooses the
# mode) with nothing else set, "restore" Standalone without an administrator and the shared server's instance_env, "reopened" an
# instance set up with sign-in off and started again in Standalone, which has no administrator, so its setup reopens (#149); each with
# folders of its own.
setup_names=(phone-fresh desktop-fresh phone-restore desktop-restore phone-reopened desktop-reopened)
setup_app() {
  local dir="${files}/setup-$1"
  mkdir -p "$dir"
  cd out
  export ASPNETCORE_ENVIRONMENT=Production Database__Provider=Sqlite Database__ConnectionString="Data Source=${dir}/tankstat.db" \
    Storage__Path="${dir}/uploads" Storage__PhotosPath="${dir}/photos" Backups__Path="${dir}/backups" \
    DataProtection__KeysPath="${dir}/keys" Settings__Path="${dir}/settings.json"
  if [[ "$1" == *-restore ]]; then export Auth__Mode=Standalone "${instance_env[@]}"; fi
  if [[ "$1" == *-reopened ]]; then
    # Set up once with sign-in off (the environment completes it), stopped, then started in Standalone. The first start is this
    # subshell's own child, which the script's cleanup does not reach: stopped here whenever the subshell ends first.
    Auth__Mode=None Setup__Enabled=false dotnet Tankstat.Api.dll --urls "http://127.0.0.1:$2" &
    local first=$!
    trap 'kill "$first" 2>/dev/null || true' EXIT
    trap 'exit 143' TERM
    answers "http://127.0.0.1:$2" "$first" || exit 1
    kill "$first" && wait "$first" || true
    trap - EXIT TERM
    export Auth__Mode=Standalone
  fi
  exec dotnet Tankstat.Api.dll --urls "http://127.0.0.1:$2"
}
for i in "${!setup_names[@]}"; do
  setup_app "${setup_names[$i]}" $((port + 1 + i)) > "${files}/setup-${setup_names[$i]}.log" 2>&1 &
  pids+=($!)
done

if ! answers "$E2E_URL" "${pids[0]}"; then
  echo "The app did not start on $E2E_URL:" >&2
  cat "${files}/app.log" >&2
  exit 1
fi
# The shared server's setup, finished without a browser: the code from its log and one setupFinish.
if ! code="$(setup_code "${files}/app.log" "${pids[0]}")"; then
  echo "The app logged no setup code on $E2E_URL:" >&2
  cat "${files}/app.log" >&2
  exit 1
fi
finish="{\"query\":\"mutation(\$i: SetupFinishInput!) { setupFinish(input: \$i) { restarting } }\",\"variables\":{\"i\":{\"code\":\"${code}\",\"email\":\"root@example.com\",\"password\":\"initial-password-1\",\"passwordConfirmation\":\"initial-password-1\"}}}"
if ! curl -sf -H 'Content-Type: application/json' -d "$finish" "$E2E_URL/graphql" | grep -q '"restarting":false'; then
  echo "The app's setup could not be finished on $E2E_URL:" >&2
  cat "${files}/app.log" >&2
  exit 1
fi
# Each waiting server's address and its one-time code, from the line its start logs, for the tests (E2E_SETUP, JSON by name). The code
# first: a reopened server's first start answers on the same port without one, and only the start that logs it waits for the tests.
setup="" sep=""
for i in "${!setup_names[@]}"; do
  name="${setup_names[$i]}" url="http://127.0.0.1:$((port + 1 + i))" log="${files}/setup-${setup_names[$i]}.log"
  if ! code="$(setup_code "$log" "${pids[$((i + 1))]}")" || ! answers "$url" "${pids[$((i + 1))]}"; then
    echo "The waiting server $name did not start on $url, or logged no setup code:" >&2
    cat "$log" >&2
    exit 1
  fi
  setup+="${sep}\"$name\":{\"url\":\"$url\",\"code\":\"$code\"}" sep=","
done
export E2E_SETUP="{${setup}}"

# Playwright runs from the web app's folder: its config, node_modules, test-results/ and playwright-report/ are there.
web="$repo/src/frontend/tankstat-react"
status=0
if [[ "${E2E_NATIVE:-}" == 1 ]]; then
  (cd "$web" && npx playwright test "$@") || status=$?
else
  version="$(cd "$web" && node -p "require('@playwright/test/package.json').version")"
  # As the host user, so nothing it writes (test-results/, the report) belongs to root; the web app's own node_modules, the image's browsers.
  # The whole repository is mounted: a test reads a backup file from tests/backend/contracts.
  docker run --rm --network host --ipc=host --user "$(id -u):$(id -g)" -e HOME=/tmp -e E2E_URL -e E2E_SETUP \
    -v "$repo:$repo" -w "$web" "mcr.microsoft.com/playwright:v${version}-noble" npx playwright test "$@" || status=$?
fi
if [[ "$status" != 0 ]]; then
  echo "The app's last log lines:" >&2
  tail -n 40 "${files}/app.log" >&2
  for name in "${setup_names[@]}"; do
    echo "The waiting server $name's last log lines:" >&2
    tail -n 20 "${files}/setup-$name.log" >&2
  done
  echo "In src/frontend/tankstat-react: the report: npx playwright show-report; a failed test's video: test-results/e2e/<test>/video.webm, its trace: npx playwright show-trace test-results/e2e/<test>/trace.zip" >&2
fi
exit "$status"
