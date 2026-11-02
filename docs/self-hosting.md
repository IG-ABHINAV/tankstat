# Self-hosting

For operators: the Docker image, the published build without Docker, and how releases reach the container registry.

## Docker

One image contains everything: the .NET API serves GraphQL and the built web app, so a single container is the whole service. It is built from the `Dockerfile` (multi-stage: Node builds the frontend, the .NET SDK publishes the API, an Alpine ASP.NET runtime runs it as an unprivileged user).

```sh
mise run docker:build            # or: docker build -t tankstat:local --build-arg VERSION=1.2.3 .
docker run -d --name tankstat -p 8080:8080 -v tankstat-data:/data \
  -e Auth__Mode=Standalone \
  tankstat:local
```

- The app listens on port 8080 and is configured through environment variables (`Database__*`, `Auth__*`, `Smtp__*`, ...; see [Configuration](configuration.md) and [Authentication](authentication.md)) and through [the app's own settings file](configuration.md#the-apps-own-settings-file) in the volume (`/data/settings.json`); an environment variable wins over the file. Without `Auth__Mode` the image uses whatever `appsettings.json` was built in (or the settings file says), so always set it explicitly (and never bake credentials into `appsettings.json`). To send traces, metrics and logs to an OpenTelemetry collector, set `OTEL_EXPORTER_OTLP_ENDPOINT` ([Telemetry](configuration.md#telemetry-opentelemetry)); without it nothing is sent.
- SQLite, the uploaded pictures (with the photos of logs), the keys that protect the sign-in cookies and the app's own settings file live in the `/data` volume (`Database__ConnectionString=Data Source=/data/tankstat.db`, `Storage__Path=/data/uploads`, `DataProtection__KeysPath=/data/keys` and `Settings__Path=/data/settings.json` by default); mount a volume to keep your data and keep people signed in across container updates. The photos of refuellings and expenses are the part that grows without limit. To keep them apart, on a bigger or cheaper disk or for file backups (restic, rsync) on their own schedule, opt in: mount a volume at `/data/photos` and set `Storage__PhotosPath=/data/photos` (`-v tankstat-photos:/data/photos -e Storage__PhotosPath=/data/photos` next to `-v tankstat-data:/data`). The next start moves the photos already in `/data/uploads` there, so mount that volume **before** setting it: a folder that is not a volume would be lost with the container. The image creates `/data/photos` for its user, so a new volume there is writable; a host folder mounted there must be writable by the image's user (uid 1654). **Upgrading to the version with access and refresh tokens signs everybody out once**: the keys of older versions were not kept, and older session cookies are replaced by the new pair at the next sign-in. Use `Database__Provider` and `Database__ConnectionString` for PostgreSQL, SQL Server or MySQL instead.
- Backups go to `/data/backups` (`Backups__Path`), a folder of the volume the image creates for its user: `docker exec tankstat dotnet Tankstat.Api.dll backup` writes one (so does **Back up now** on the administration's **Backups** page, which also lists, uploads, downloads and restores them), encrypted once `Backups__Encryption__Recipient` and `Backups__Encryption__Salt` are set (`backup keygen` prints them). An upgrade backs the data up by itself before it migrates the database (a start that cannot write that backup migrates nothing and stops, saying why). To keep one of your own until the new release has proved itself, write it under a name of its own (`backup --output /data/backups/before-upgrade.zip`), since retention removes the `tankstat-backup-...` files, the command's too. For one every night, choose a time on the *Server settings* page's Backups tab, or set `Backups__Every=02:00` with `Backups__Keep=14` (or `Backups__KeepDays`), and keep room in the volume for one backup more than that; the time is the container's, UTC unless you pass `-e TZ=<your zone>`. A restore asked for from the app restarts the server inside the container, which keeps running; see [Backup and restore](backup-and-restore.md).
- A new instance waits for its [setup](authentication.md#the-first-start-setup), which makes the first administrator: open the address and enter the one-time code from `docker logs tankstat` (the line that starts with "Setup is waiting"). The same page can restore a backup copied into `/data/backups` first, which is how a server is moved to a new host. `Setup__Enabled=false` skips it, except in `Standalone` without an administrator, where the start stops instead.
- Migrations are applied when the container starts. The container has a health check (the GraphQL endpoint).
- `VERSION` (build argument) is the version of the web app and of the API, both shown in the footer (the web app's even while the server is out of reach); `REVISION` and `CREATED` become OCI image labels.
- Terminate TLS in front of the container with your reverse proxy (see the [ProxyHeader mode](authentication.md#proxy-header-settings-auth__modeproxyheader) for proxy-based sign-in). Installing the web app on a phone, and the service worker behind it, need that HTTPS; the API serves `/manifest.webmanifest` and `/sw.js` from `wwwroot` with the cache rules described under [Install as an app](install-as-an-app.md), and `scripts/docker-smoke.sh` checks them. Keep those rules: the proxy must not set a `Cache-Control` of its own on `/sw.js`, `index.html` or the other fixed names (a cached `sw.js` keeps every browser on the old release).
- The image builds for amd64 and arm64 with buildx without emulation (`docker buildx build --platform linux/amd64,linux/arm64 .`).

## Releases (container registry)

`.github/workflows/release.yml` publishes the image to the GitHub Container Registry as `ghcr.io/<owner>/<repo>`. Create a GitHub release with a tag like `v1.2.3` (or `v1.2.3-rc.1`): the workflow first runs the whole CI as a gate (its build job publishes the app once, stamped with the release's version), makes the image from that very build (Dockerfile target `release`: nothing is compiled again), pushes it for amd64 and arm64 with a provenance attestation and an SBOM under its digest only, pulls it back and checks it with `scripts/docker-smoke.sh` (web app, API, version, database, where pictures and photos land, also with the photos on a volume of their own, non-root, and in a second container in Standalone mode two settings an administrator saves into `/data/settings.json`, one that counts at once and one that counts after a restart in the process), and only then tags it, so the tags always point at the image that passed. A release `v1.2.3` is tagged `1.2.3`, `1.2` and `1` (`latest` for stable releases; pre-releases only get their full version; `0.x` releases get no bare major tag). "Run workflow" on the Release workflow builds and smoke-tests from any branch and pushes a `dev-<sha>` image only if you tick *push*.

```sh
docker pull ghcr.io/<owner>/<repo>:1.2.3
mise run docker:build && mise run docker:smoke      # the same smoke test, locally
```

The first publish creates the package as private and linked to the repository; change its visibility in the package settings if the image should be public.

## Without Docker

Build the self-hostable output yourself and run it with the .NET runtime (the toolchain comes from [mise](development.md#prerequisites)):

```sh
mise run build
dotnet out/Tankstat.Api.dll --urls http://0.0.0.0:8080
```

`dotnet out/Tankstat.Api.dll backup` (and `decrypt`, `backup keygen`) run the [backup commands](backup-and-restore.md) with the same settings instead of the server. `out/` contains the published API with the built frontend in `wwwroot`, including the web app's `manifest.webmanifest`, `sw.js` and `workbox-*.js`. Unknown paths fall back to `index.html` for client-side routing (the service worker does the same in the browser, never for `/graphql`, `/auth`, `/media` or `/imports`); `/assets/*` is sent as immutable, everything with a fixed name as `no-cache`.
