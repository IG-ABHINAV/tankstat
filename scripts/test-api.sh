#!/usr/bin/env bash
# Starts the API on a throwaway port, runs the contract tests against it, always cleans up.
set -euo pipefail

port="${TANKSTAT_API_PORT:-5099}"
export TANKSTAT_API_URL="http://localhost:${port}"

dotnet build src/backend/Tankstat.Api -c Release --nologo -v q
# Schema introspection (used by the contract tests) is Development-only in HotChocolate.
export ASPNETCORE_ENVIRONMENT=Development
# The contract tests assume no authentication, whatever appsettings.json says, and an instance that is not waiting for its setup.
export Auth__Mode=None Setup__Enabled=false
# Throwaway DB so the run never touches a developer database.
db="$(mktemp -u -t tankstat-api-XXXXXX.db)"
export Database__ConnectionString="Data Source=${db}"
# Throwaway upload folders too: the app moves photos between them at start, so it must never be pointed at a developer's own.
files="$(mktemp -d -t tankstat-api-files-XXXXXX)"
export Storage__Path="${files}/uploads" Storage__PhotosPath="${files}/photos"
# And a settings file of the run's own (none exists), so the working directory's (the repository root) is never read.
export Settings__Path="${files}/settings.json"
# A backups folder of its own and no schedule or retention of the developer's: a start writes a missed backup and prunes the folder.
export Backups__Path="${files}/backups"
unset Backups__Every Backups__Days Backups__Keep Backups__KeepDays
# Nor a telemetry endpoint of the developer's (.NET reads the names in any case).
for name in $(compgen -e); do
  case "${name^^}" in OTEL_* | TELEMETRY__*) unset "$name" ;; esac
done
dotnet src/backend/Tankstat.Api/bin/Release/net10.0/Tankstat.Api.dll --urls "$TANKSTAT_API_URL" &
pid=$!
trap 'kill "$pid" 2>/dev/null || true; rm -f "$db"; rm -rf "$files"' EXIT

for _ in $(seq 1 50); do
  curl -sf -H 'Content-Type: application/json' -d '{"query":"{ __typename }"}' "$TANKSTAT_API_URL/graphql" >/dev/null && break
  sleep 0.2
done

dotnet test tests/backend/Tankstat.Api.ApiTests
