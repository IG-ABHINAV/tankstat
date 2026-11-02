#!/usr/bin/env bash
# Takes the README's screenshots: starts the published build (out/) on a throwaway port with sign-in off and the seeder's data, runs
# src/frontend/tankstat-react/e2e/screenshots.ts against it (playwright.screenshots.config.ts: phone and desktop, dark and light) into
# docs/screenshots, always cleans up. Playwright runs as scripts/test-e2e.sh runs it: natively with E2E_NATIVE=1, otherwise in the
# Playwright image of the installed version, the reference: natively the fonts are the machine's. Arguments go to `playwright test`
# (scripts/screenshots.sh --project desktop-dark); without any, every picture is taken again and the folder emptied first.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo"
if [[ ! -f out/Tankstat.Api.dll ]]; then
  echo "No build in out/: run 'mise run build' first (mise run screenshots does)." >&2
  exit 1
fi

# Away from scripts/test-e2e.sh's servers (5097 and the six after it), so the two can run side by side.
port="${E2E_PORT:-5110}"
export E2E_URL="http://127.0.0.1:${port}"
if curl -s -o /dev/null "$E2E_URL"; then
  echo "Something already answers on $E2E_URL: stop it, or choose another port with E2E_PORT." >&2
  exit 1
fi

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

files="$(mktemp -d -t tankstat-screenshots-XXXXXX)"
trap 'kill "${pid:-}" 2>/dev/null || true; wait "${pid:-}" 2>/dev/null || true; rm -rf "$files"' EXIT

# A few vehicles with a couple of years of logs each and a drawn picture each (never a downloaded photo: those carry licences of their
# own, and this needs no network). The seed fixes the vehicles, the amounts and the pictures, but the dates follow the day of the run
# (the last fill-up is recent), so another day gives other dates, overdue days and monthly figures in every picture.
Auth__Mode=None dotnet run --project src/backend/Tankstat.Seeder -- --yes \
  --connection "Data Source=${files}/tankstat.db" --uploads "${files}/uploads" --photos "${files}/photos" --settings "${files}/settings.json" \
  --vehicles 4 --refuelings 40-70 --recurring 2-4 --expenses 6-12 --notifications 4 --seed 2026 --drawn-pictures

# From out/: the content root (wwwroot, the web app) is the working directory, not the dll's folder. An instance that holds data counts
# as set up, and with sign-in off everything the seeder made is on the page.
(
  cd out
  ASPNETCORE_ENVIRONMENT=Production \
    Database__Provider=Sqlite Database__ConnectionString="Data Source=${files}/tankstat.db" \
    Storage__Path="${files}/uploads" Storage__PhotosPath="${files}/photos" Backups__Path="${files}/backups" \
    DataProtection__KeysPath="${files}/keys" Settings__Path="${files}/settings.json" \
    Auth__Mode=None Setup__Enabled=false \
    exec dotnet Tankstat.Api.dll --urls "$E2E_URL"
) > "${files}/app.log" 2>&1 &
pid=$!

# This app's own start (its log says where it listens), not whatever else answers on the port meanwhile.
for _ in $(seq 1 150); do
  if grep -q "Now listening on: $E2E_URL" "${files}/app.log"; then break; fi
  if ! kill -0 "$pid" 2>/dev/null; then break; fi
  sleep 0.2
done
if ! grep -q "Now listening on: $E2E_URL" "${files}/app.log" || ! kill -0 "$pid" 2>/dev/null || ! curl -sf -H 'Content-Type: application/json' -d '{"query":"{ __typename }"}' "$E2E_URL/graphql" >/dev/null; then
  echo "The app did not start on $E2E_URL:" >&2
  cat "${files}/app.log" >&2
  exit 1
fi

# A full run replaces the whole set, so no picture of an earlier day or of a page no longer taken stays; a partial one keeps the rest.
if [[ $# == 0 ]]; then
  rm -f docs/screenshots/*.png
else
  echo "Taking only some of the pictures: the others keep an earlier run's data and dates." >&2
fi

web="$repo/src/frontend/tankstat-react"
status=0
if [[ "${E2E_NATIVE:-}" == 1 ]]; then
  (cd "$web" && npx playwright test --config playwright.screenshots.config.ts "$@") || status=$?
else
  version="$(cd "$web" && node -p "require('@playwright/test/package.json').version")"
  # The app ships no font of its own, and the image has none of its list but a CJK one, which fontconfig would pick for all of them:
  # Liberation Sans (Arial's measures) for the whole list instead, so the pictures have the same Latin letters on every machine.
  cat > "${files}/fonts.conf" <<'XML'
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "fonts.dtd">
<fontconfig>
  <include>/etc/fonts/fonts.conf</include>
  <alias binding="same"><family>sans-serif</family><prefer><family>Liberation Sans</family></prefer></alias>
  <alias binding="same"><family>system-ui</family><prefer><family>Liberation Sans</family></prefer></alias>
  <alias binding="same"><family>Roboto</family><prefer><family>Liberation Sans</family></prefer></alias>
  <alias binding="same"><family>Helvetica Neue</family><prefer><family>Liberation Sans</family></prefer></alias>
  <alias binding="same"><family>Segoe UI</family><prefer><family>Liberation Sans</family></prefer></alias>
</fontconfig>
XML
  # As the host user, so the pictures belong to them; the whole repository is mounted, since they land in docs/screenshots.
  docker run --rm --network host --ipc=host --user "$(id -u):$(id -g)" -e HOME=/tmp -e E2E_URL \
    -v "${files}/fonts.conf:/tmp/fonts.conf:ro" -e FONTCONFIG_FILE=/tmp/fonts.conf \
    -v "$repo:$repo" -w "$web" "mcr.microsoft.com/playwright:v${version}-noble" npx playwright test --config playwright.screenshots.config.ts "$@" || status=$?
fi
if [[ "$status" != 0 ]]; then
  echo "The app's last log lines:" >&2
  tail -n 40 "${files}/app.log" >&2
fi
exit "$status"
