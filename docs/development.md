# Development

For people who work on Tankstat itself: the toolchain, the layout of the repository, how to run and test it, and the tools around it. [CONTRIBUTING.md](../CONTRIBUTING.md) is the entry point for a contribution: what a change needs, how work is tracked in the issues, and where to start.

## Prerequisites

[mise](https://mise.jdx.dev) manages the toolchain (Node LTS and .NET 10, see `mise.toml`).

```sh
mise install
mise run install   # npm ci (in src/frontend/tankstat-react) + dotnet restore
```

## Layout

```
src/backend/Tankstat.Api/                 HTTP host: HotChocolate GraphQL at /graphql, static SPA hosting
src/backend/Tankstat.Application/         use cases and ports (e.g. IDatabaseProbe)
src/backend/Tankstat.Domain/              entities (Vehicle, Refueling, User, access grants) and their rules, no dependencies
src/backend/Tankstat.Infrastructure/      EF Core mapping, repositories, database provider selection
src/backend/Tankstat.Migrations.*/        one EF Core migration set per provider (Sqlite, PostgreSql, SqlServer, MySql)
src/frontend/tankstat-react/              the web app, one npm package: package.json, node_modules, Vite/Vitest/Playwright/codegen configs
src/frontend/tankstat-react/src/          React + Vite app: top bar, hamburger menu, pages (vehicles, trash, account, administration); UI built with MUI (@mui/material, with MUI X grid, charts and date pickers), lucide-react icons, motion animations, react-i18next
tests/backend/Tankstat.Domain.UnitTests/          entity rules
tests/backend/Tankstat.Application.UnitTests/     services (incl. access and auth rules) against in-memory fakes
tests/backend/Tankstat.Infrastructure.UnitTests/  config binding, repositories (SQLite file), migration/model consistency for every provider
tests/backend/Tankstat.Api.IntegrationTests/      in-process GraphQL tests for every auth mode (WebApplicationFactory)
tests/backend/Tankstat.Api.ApiTests/              black-box contract tests against a running server
src/frontend/tankstat-react/tests/        Vitest unit (*.unit.test.*) and integration (*.integration.test.*) tests, a folder per feature area
src/frontend/tankstat-react/tests/support/  shared test helpers: msw handlers (server.ts; GraphQL ones through graphql.ts), fake backends (mocks.tsx), setup
src/frontend/tankstat-react/e2e/          real-browser tests (Playwright, *.e2e.ts) against the published build; support/ holds the sign-in, GraphQL and layout helpers
scripts/test-api.sh                       starts the API, runs the API tests, stops it
scripts/test-e2e.sh                       starts the build in out/, runs the real-browser tests, stops it
tests/backend/contracts/openai-compatible/  answers as OpenAI-compatible model servers write them, for the app's adapter tests
```

Photo reading is optional: the app talks to a vision model behind an OpenAI-compatible API through its `IRecognitionProvider` port (see [Reading photos with an OpenAI-compatible model](photo-reading.md)).

Backend dependencies point inward: Api -> Application, Infrastructure; Infrastructure -> Application; Application -> Domain. Application defines interfaces (ports) that Infrastructure implements, so Application and Domain never reference EF Core or HotChocolate.

Everything frontend lives in `src/frontend/tankstat-react`: `package.json` and its lockfile, `node_modules`, the configs, the app (`src/`), its Vitest tests (`tests/`) and Playwright tests (`e2e/`). Run npm and npx there; the mise tasks do it for you. `schema.graphql` stays at the repository root, where the API exports it, and the web app's build goes to `src/frontend/tankstat-react/dist`.

## Running the app

Run both in separate terminals:

```sh
mise run dev:api   # API with hot reload on http://localhost:5080 (GraphQL IDE at /graphql)
mise run dev:web   # Vite dev server, proxies /graphql, /auth/oidc/, /media and /imports to :5080
```

To watch the app's telemetry (traces, metrics and logs; [Configuration](configuration.md#telemetry-opentelemetry)), run a collector UI and point the API at it. The .NET Aspire dashboard takes OTLP over gRPC and prints a sign-in link in its log:

```sh
docker run --rm -it -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard   # then open http://localhost:18888
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 mise run dev:api
```

The test hosts never export: they drop a developer's `OTEL_*` and `Telemetry__*` variables.

## Testing

```sh
mise run test              # typecheck, lint, then all test layers
mise run test:unit         # frontend + backend unit tests
mise run test:integration  # frontend (msw-mocked HTTP) + backend (in-process)
mise run test:api          # contract tests against a freshly started server
mise run test:coverage     # unit + integration tests with coverage reports in ./coverage
mise run test:e2e          # real-browser tests against a fresh build (not part of mise run test)
```

### Real-browser tests

`src/frontend/tankstat-react/e2e/*.e2e.ts` open the published app in Chromium with [Playwright](https://playwright.dev), at phone size (Pixel 7: 412 px, touch) and desktop size (1280 × 900), and check what the Vitest tests in jsdom cannot see: that a page never scrolls sideways, that no value runs past its panel or under its help button, and that on a touch screen every control is at least 44 px; axe in the real browser, which also judges the colour contrast of the real styles; and the user flows across the real server, one file per area (logs, vehicles, account, administration, sharing and notifications, import, offline, the screens' own settings, the setup of a new instance), each as a user of its own. Every new user flow gets its test here (CLAUDE.md). `mise run test:e2e` builds `out/` (`mise run build`) and runs `scripts/test-e2e.sh`, which starts the build from `out/` on port 5097 (Standalone, its setup finished by the script with the one-time code from its log, throwaway folders, none of your own `Auth__`, `Storage__`, `Backups__` or other settings) and six instances waiting for their setup on the next ports (one per setup test and size, since a finished setup cannot be undone; two of them set up with sign-in off first and switched to Standalone, so their setup reopens), runs the tests and stops them again. Options after `--` go to Playwright:

```sh
mise run test:e2e                              # everything, both sizes
scripts/test-e2e.sh --project phone -g Backups # again on the same out/, only the phone and the tests named "Backups"
```

Playwright runs in Docker here, in the `mcr.microsoft.com/playwright` image of the installed `@playwright/test` version (it supports only some Linux distributions as a host), as your own user and with the web app's own `node_modules`; CI runs it natively (`E2E_NATIVE=1`). So Docker must be running, and the first run pulls the image (about 1 GB). A failed test keeps a video, a trace and a screenshot in `src/frontend/tankstat-react/test-results/e2e/<test>/` (`video.webm` plays in any browser; on CI they are in the `e2e-results` artifact):

```sh
cd src/frontend/tankstat-react
npx playwright show-report                                   # the HTML report of the last run (playwright-report/)
npx playwright show-trace test-results/e2e/<test>/trace.zip  # a failed test step by step: the page, the network, the console
```

Each test creates the data it needs through GraphQL, since all workers share one server; it never counts what other tests made.

The backup encryption is the age format: `tests/backend/contracts/age/` holds files written by age 1.3.2 (see its README), which the reader must open, and `AgeStreamTests.TheRealTool_OpensWhatTheWriterWrote` runs `age -d` on the writer's file when `age` runs here (`age` on the `PATH`, or mise's installed copy: `mise install age@1.3.2`; the test runs mise offline, so it never fetches or installs anything); without it that one check passes with a line saying it was skipped.

CI has no database servers, so the dump and the load are also checked against real ones by `ProviderRoundTripTests` (Infrastructure unit tests), which run for a provider when a connection string for it is in the environment, `{db}` standing for a database name the test makes and drops, and otherwise pass with a line saying they were skipped. For example, with throwaway containers:

```sh
docker run -d --name tk-pg -e POSTGRES_PASSWORD=secret -p 55432:5432 postgres:17-alpine
docker run -d --name tk-mysql -e MYSQL_ROOT_PASSWORD=secret -p 53306:3306 mysql:8.4
docker run -d --name tk-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Str0ng!Passw0rd' -p 51433:1433 mcr.microsoft.com/mssql/server:2022-latest

TANKSTAT_TEST_POSTGRESQL='Host=localhost;Port=55432;Database={db};Username=postgres;Password=secret' \
TANKSTAT_TEST_MYSQL='Server=localhost;Port=53306;Database={db};User=root;Password=secret' \
TANKSTAT_TEST_SQLSERVER='Server=localhost,51433;Database={db};User Id=sa;Password=Str0ng!Passw0rd;TrustServerCertificate=True;Encrypt=False' \
  dotnet test tests/backend/Tankstat.Infrastructure.UnitTests --filter FullyQualifiedName~ProviderRoundTripTests
```

Each provider's tests back up a seeded database, load the backup into another, and compare the two backups line for line; and move a backup from SQLite to the server and back, comparing the values (numbers without trailing zeros, timestamps to the microsecond). Run them after changing the dump or the load, a database driver or EF Core.

The API tests pin the GraphQL schema the frontend relies on, so a breaking schema change fails them. They read the target from `TANKSTAT_API_URL` (default `http://localhost:5080`); `test:api` sets it for you.

Frontend tests are named `*.unit.test.ts(x)` (a function or component on its own) or `*.integration.test.ts(x)` (the app against msw-mocked GraphQL); other names are not picked up. They are grouped by feature area, unit and integration tests side by side, nested where an area has parts:

```text
src/frontend/tankstat-react/tests/
  support/                   shared helpers: msw default handlers (server.ts), fake backends (mocks.tsx), setup.ts
  app/  app/apollo/  app/i18n/   the shell, error handling, accessibility, Apollo, translations
  auth/  pwa/                sign-in, installing the app
  home/                      the home page and its vehicle cards
  vehicles/                  vehicle list and page; vehicles/logs/, vehicles/recurring/, vehicles/dashboard/ for its tabs
  recognition/  pictures/    photo reading, pictures and uploads
  trash/  grid/  settings/  notifications/  admin/  import/
  ui/                        the theme, forms, dialogs, toast and other shared building blocks
```

A new test goes into the folder of the feature it covers; run one file from `src/frontend/tankstat-react` with `npx vitest run tests/<area>/<file>`.

## Data model and migrations

A vehicle fuel log: `Vehicle` (name, licence plate, fuel type, units, optional picture) and `Refueling` (date, volume, a `Cost`, an `OdometerReading`, full tank, whether a fill-up before it was missed, optional note, who logged it). `OdometerReading`, `Cost` and the units are shared building blocks meant to be reused by later entities (inspections, service fees). Exposed via GraphQL (`vehicles`, `vehicle(id)`, `refuelings(vehicleId, ...)`, `refuelingTrash`, `vehicleLogAccess`, `shareCandidates`, `logDefaults`; mutations such as `addVehicle`, `logRefueling`, `updateRefueling`, `deleteRefueling`, `restoreRefueling`, `setVehicleLogAccess`, `emptyTrash`, `emptyRefuelingTrash`).

Tyres (#287): `TyreSet` (a set's name, season, size, *in use since*, the DOT made date, a rough figure for what it ran before it was tracked), `TyreInstallation` (a set's time on the car: its first day, the day it came off, which of its two pairs was at the front) and `TyreLogDay` (one per vehicle and day, owning the `OdometerReading` typed with a swap). Distances are worked out on read from the vehicle's readings (`TyreCalculator`), never stored; the history's rules are `TyreHistory`. GraphQL: `vehicleTyres(vehicleId)`, `tyreTrash`, and a mutation per change answering with the vehicle's tyres; the offline download carries the vehicle's tyres whole on its first page.

Adds can carry an id chosen by the client (`id` on `AddVehicleInput`, `LogRefuelingInput`, `AddExpenseInput`, `AddRecurringExpenseInput`; `expenseId` on `MarkRecurringExpensesDoneInput`): sending the same add again, after an answer that never arrived, answers with what the first one created instead of creating it twice (an id someone else used is refused with `sync.idTaken`). The app's add dialogs choose one per opening, so pressing Save again after a network error never logs a refuelling twice. Vehicles, refuelings, expenses and recurring expenses also count their saves (`version`, from 1); this is the groundwork for working offline and syncing later, where a change made from an old version is detected instead of silently overwriting what someone else saved.

What a repeated add does, in detail:

- It answers with **what the first save stored**: values sent again are not compared, so an edit made between a lost answer and the retry is not applied (edit the entry afterwards). Steps after the save that may not have finished (attaching the photos still waiting as drafts, taking a photo reading, recalculating the consumption) run again, since they are safe to repeat.
- The id is looked up across the whole instance, so a user who sends an id **someone else** already used gets `sync.idTaken`; that tells them such an id exists. Ids are random UUIDs, so this cannot be used to discover anything; it is the one place where something invisible does not simply look missing.
- *Mark as done* is a repeat only when its expense id is already linked to **every** schedule asked for; an id linked to other schedules is another visit and is refused (`sync.idTaken`). A *Mark as done* without an amount logs no expense and has no id: sending it again starts the same interval again, which changes nothing but the version.
- Every update or trash is a conditional write: it is saved only if the stored row still has the version the entity was loaded with (`Version` is a concurrency token; `SyncInterceptor` supplies the loaded version), so two changes made from the same version cannot both be saved; the loser gets `sync.versionMismatch`, also online.

What devices download for offline use (the server side; the app's side comes with the next steps):

- `updatedAt` on vehicles, refuelings, expenses and recurring expenses marks anything a device must download again, unlike `version`, which only counts what a writer can conflict with. It is set on every save by one EF interceptor (`SyncInterceptor`), and by the few writes that bypass it: the consumption recalculation, a log's photos added or removed, a schedule's changes for the expenses that list it, and a user's data moved to someone else. Rows saved before it existed carry 2000-01-01.
- Removing for good (an emptied trash, a deleted schedule, a purged vehicle or user) leaves a tombstone (`Tombstones`: the entity's type and id, its vehicle, when), written in the same save by the same interceptor; a vehicle's stands for everything below it. They are kept for `Sync:TombstoneRetentionDays`.
- `offlineChanges(input: { vehicleId, from, since, after, take })` pages through a vehicle (View on its logs). A first download (no `since`) brings the logs dated from `from` (all of them without it), the trash included for those who may change the logs (anyone who may only view them gets a log in the trash as `removed`, as online they do not see the trash); a later one (`since` = the `watermark` the previous download returned) brings every row saved since then, minus a two-minute overlap for saves that committed late, whatever its date, plus `removed`. Refuelings and expenses are paged each by its own key, `(updatedAt, id)` in the database's own order of ids (providers order uuids differently; the database both orders and compares, a test prints every provider's SQL), inside one opaque `next`. The vehicle comes with every page, all its schedules (with today's status) and its tyres whole (`tyres`, the Tyres tab's answer; no tyre tombstones in `removed`) with the first. A `since` older than the tombstones kept answers `resync: true`.
- `offlineSettings` / `updateOfflineSettings` keep the user's offline window with the account (a default and per-vehicle rules: `none`, `all`, `thisYear`, `thisAndLastYear`, `from:yyyy-MM-dd` or `span:` an ISO 8601 duration such as `P2Y6M4DT2H48M12S`; default `span:P2M`); a device turns a rule into a date on its own clock. `Vehicle.logCountSince(from)` counts what a download would bring.

- `session { instance { generation restorePoint } }` says which history the data belongs to: the `InstanceState` table's one row, whose `Generation` a restore stamps anew after the rows are loaded (never the backup's). A device keeps the first generation it sees and, on another one, sets its downloaded rows aside (`foundRows.ts`), drops the downloaded window and the kept answers, and downloads afresh; its outbox is sent as it is. Every download asks `session { instance { generation } }` first (`ServerGeneration`) and starts afresh before it writes anything when that changed, since a device may get no `Session` answer after a restore; its cursors carry the generation they were written in. The restore itself is `RestoreStep` at start (`docs/backup-and-restore.md`), asked for by `restoreBackup` or `tankstat restore`.

Sending the changes a device kept: `syncChanges(input: { changes: [ChangeInput] })` (1 to 200 per request, in the order they were made). A `ChangeInput` is the change's own id, the version it was made from (`expectedVersion`) and exactly one operation, with the same input as the single mutation (`logRefueling`, `updateExpense`, `deleteVehicle`, `markRecurringExpensesDone`, the 15 tyre changes from `addTyreSet` to `setTyreLogDay`, ...; an add names its id, a swap the id of the installation it creates). A device sends a tyre change without a version when a change waiting before it already changed that set or installation (the server will have bumped it), and never folds tyre changes: each waits as it was made, since a swap depends on the ones before it. The device shows them done by the server's own rules, ported to `src/tyres/tyreHistory.ts` and pinned to `TyreHistory` by the shared cases in `tests/backend/contracts/tyre-swaps/tyre-swaps.json`. Each change goes through the same service as its mutation (every rule and access check) and is answered `APPLIED` (with the entity and its new version) or `PARKED` (with the error key and arguments the mutation would have given; one leniency: a tyre swap, past swap or change of positions whose set is on the car that day already with the pairs it asks for is answered as the retry it is, since a swap that joined an installation left no id to find, while the same with other pairs is `tyres.alreadyOnCar` as online; applied anyway it is never a retry); the server keeps both in `SyncChanges`, so a change sent again (an answer that never arrived) is answered with what happened the first time and applied once. Applied records go after `Sync:RetentionDays`; parked ones stay for a person to decide. One batch of a user at a time. A change is kept as the `ChangeInput` it was (JSON, enums in the API's spelling).

Photos kept on a device go up as drafts (`PUT /media/vehicles/{id}/photo-drafts`, with `?form=&locale=` for the photos of a new entry, so they are read) right before the change they belong to, which names the draft ids: in `photoIds` of an add or a visit, or as one of the four photo operations of a saved log, `addRefuelingPhoto` / `addExpensePhoto` (`{ logId, draftId }`: Edit on the log's vehicle, one of the sender's drafts of that vehicle that has not expired, room on the log; a draft already attached to that log is answered with its id) and `removeRefuelingPhoto` / `removeExpensePhoto` (`{ logId, imageId }`). The log's version does not move with its photos, so they are applied without one. A vehicle's picture goes the same way: `setVehiclePicture` (`{ vehicleId, draftId }`: Edit on the vehicle itself, one of the sender's unexpired drafts of that vehicle; the draft becomes the picture and the previous picture is deleted, as with an upload; the same draft again is answered as done) and `removeVehiclePicture` (the vehicle's id). A picture is not versioned either: the last one wins, as online. Applied anyway from *Not applied* (`resolveSyncChange`) by someone else who may decide about it, a photo or picture change takes its sender's draft, while that has not expired. A device sends a change with photos in a request of its own (at most 20 drafts per person and vehicle) and uploads a draft again only after 20 hours (drafts expire after a day).

Deciding about parked changes: `parkedChanges` lists the parked changes of every vehicle whose logs the user may see, and those they sent (`SyncChangeInfo`: kind, `change` as JSON, `base` (below), `reason`, `vehicle { id name distanceUnit volumeUnit }`, `submittedBy`, `canResolve`, `current`); `Vehicle.parkedChangeCount` counts them per vehicle. A `ChangeInput` may carry `base`: JSON of the values the change was made from, in its input's shape, as the device held them (at most 4000 characters, kept apart from the change and only while it is parked; the server never reads it). `current` is what an edit, trash or restore concerns as it is on the server now: `state` (`LIVE`, `TRASHED`), `version`, who changed it last, when and how (`changedBy`, `changedAt`, `lastChange`: `CREATED`, `EDITED`, `TRASHED`, `RESTORED`, `DONE`, `FILLED_FROM_PHOTO`; the same three fields are on `Vehicle`, `Refueling`, `Expense` and `RecurringExpenseInfo`) and exactly one of `vehicle`, `refueling`, `expense`, `schedule`, `tyreSet`, `tyreInstallation` (an installation of a set in the trash is in the trash with it). It is null for adds, visits and photos, for what is gone (purged, a deleted schedule, a log of a vehicle in the trash), and for what the reader may not see: View on the vehicle's logs for what is live, Edit for what is in the trash, whoever sent the change. `resolveSyncChange(input: { id, action: APPLY | DISCARD, change, restoreFirst })`, for whoever sent it or has Edit on the vehicle's logs (`sync.cannotResolve`), applies it anyway (without its `photoIds`, and without its `expectedVersion` unless `change` gives one: an edit merged with `current` is applied against `current.version` and parked again with `sync.versionMismatch` when it moved meanwhile; `change`, of the same kind (`sync.kindMismatch`), replaces it first; `restoreFirst`, only for an edit of a refuelling, expense or vehicle (`sync.cannotRestoreFirst`), restores it from `change.expectedVersion` and then applies the edit, and puts it back in the trash when the edit is refused) or discards it; a trash sent with `syncChanges` (or applied this way) for what is already in the trash is answered as applied when the sender may restore it, since there is nothing left to do; a refusal keeps it parked with the new reason and is the mutation's error, and a change someone else decided meanwhile is `sync.notParked` (the decision is a conditional write). Everyone who may see a parked change as themselves gets a `SYNC_CHANGE_PARKED` notification (derived when they read their notifications, like the recurring reminders) until someone decides.

Pending migrations are applied automatically at startup. Each provider has its own migration project, so after changing an entity run:

```sh
mise run db:migration AddSomething   # adds the migration to all four providers
```

A unit test fails if any provider's migrations are out of sync with the model.

## GraphQL types are generated

Nothing GraphQL is typed by hand. `schema.graphql` is exported from the API (`mise run schema:export`) and, with the operations in `src/frontend/tankstat-react/src/graphql/*.graphql`, GraphQL Code Generator writes `src/frontend/tankstat-react/src/gql/generated.ts`. After changing the API schema or a `.graphql` document run `mise run codegen` and commit the results; `mise run test` (and an API test) fail if they are stale.

## Import formats

Importing lives in `Application/Imports`: one `IImportParser` per file format turns an uploaded file into an `ImportBatch` (vehicle, fuel logs, expenses, recurring reminders), and `ImportService` keeps the batch in memory for the preview and commits it row by row through the same services the dialogs use, so every rule applies and a bad row is reported rather than fatal. `FuelioCsvParser` is the first format. A new format is a new parser with its own tests, and its name added to `FORMATS` in `src/frontend/tankstat-react/src/pages/ImportPage.tsx` (with the `import.file.formats.<name>` texts in both languages).

## Languages

The UI is in English and Hungarian (react-i18next). Every visible string is in `src/frontend/tankstat-react/src/i18n/locales/<lang>.json` (typed keys; a test checks that all languages have the same messages and placeholders). API errors carry a stable `key` and `args` (e.g. `password.tooShort`, `{min: 10}`) which the UI translates; the API's English message is only a fallback. Dates and numbers use `Intl` for the selected language. To add a language: add `<lang>.json`, list it in `LANGUAGES` (`i18n/index.ts`), and add it to the catalogs of `src/frontend/tankstat-react/tests/app/i18n/I18n.unit.test.ts`; the test then tells you what is missing (it fails until the language in `LANGUAGES` has its catalog there).

## Test data (seeder)

`Tankstat.Seeder` is a standalone console tool that does only one thing: **deletes the database, creates it, migrates it and fills it with random, consistent test data**. For the no-authentication setup only: it creates no users (everything belongs to the anonymous owner) and refuses to run when `Auth:Mode` is anything but `None`, wherever it is set: in the environment or in the app's own settings file, which it reads below the environment as the app does (`--settings`, else `Settings__Path`, else `settings.json` in the working directory).

```sh
mise run seed                                                       # 25 vehicles, 20 refuelings each, into the dev:api database
mise run seed -- --vehicles 500 --trashed 40 --refuelings 5-60 --seed 7
mise run seed -- --help
```

| Option | Meaning |
| --- | --- |
| `--vehicles <n>` | vehicles to create (default 25) |
| `--trashed <n>` | additional vehicles that are in the trash (default 0) |
| `--refuelings <n\|a-b>` | refuelings per vehicle, a number or a range (default 20) |
| `--recurring <n\|a-b>` | recurring expenses per vehicle, at most 5 (default 0-2) |
| `--expenses <n\|a-b>` | one-off expenses per vehicle, besides the visits of its recurring expenses, at most 1,000 (default 0-5) |
| `--notifications <n>` | notifications about access changes (default 15) |
| `--seed <n>` | same seed, same data (default 1234) |
| `--provider`, `--connection` | override the database (otherwise `Database__*` settings; `mise run seed` targets the `dev:api` SQLite file) |
| `--uploads <folder>` | the folder of uploaded pictures, which is deleted too because nothing would point to the pictures any more, and where seeded pictures go (default: `Storage:Path` if configured; with pictures, the app's default `uploads` otherwise) |
| `--photos <folder>` | the folder of the photos of logs, deleted too for the same reason, when they have one of their own (default: `Storage:PhotosPath` if set; unset, they are in the uploads folder) |
| `--settings <file>` | the app's own settings file to read, for its `Auth:Mode` and folders (default: `Settings:Path`, else `settings.json` in the working directory; `mise run seed` names the `dev:api` file) |
| `--pictures` | a picture for every vehicle, the trash's too: a photo of its model from Wikimedia Commons, else (offline, nothing found) a drawn one |
| `--drawn-pictures` | a drawn picture for every vehicle (a car in a colour of its own on a road), without the network |
| `--pictures-cache <folder>` | where `--pictures` keeps each model's search results and the photos it downloaded (default: `tankstat-seeder/pictures` in the user's local application data, `~/.local/share` on Linux) |
| `--yes` | skip the "type yes" confirmation before the database is deleted |

The fuel logs are consistent: per vehicle, dates and the odometer only increase, the last fill-up is recent, litres follow the distance at a per-vehicle consumption (now and then a fill-up was not logged: the odometer runs on and the next log is marked), and prices drift slowly; a fifth of the fill-ups had a discount of one to three per cent taken off at the till (the cost is what was paid, the discount is stored with it). Trashed vehicles keep their fuel logs.

Pictures are random but deterministic: each vehicle draws from a random sequence of its own, made from the seed and its name, so the same seed gives every vehicle the same picture again (a drawn one's colours, or which of its model's photos it gets: vehicles of one model get different ones). The photos are the first JPEG and PNG results of a Commons search for the model; the search's results and the photos are cached, so a seed again needs no network and gives the same photos even if Commons' search has changed meanwhile (empty the cache for new ones). They are stored as the app stores an upload. **Commons photos carry licences of their own** (mostly CC BY-SA, which asks for attribution when shared): they are for a local test database only, never committed or published.

Each live vehicle with a fuel log gets a summer and a winter tyre set, each with a rough figure for its time before the log, swapped around the start of April and of November from the log's first day to today through the app's own history rules, each set's pairs exchanged front to rear every other season; no reading is typed, so the distances come from the log.

Recurring expenses (insurance, vignette, inspection, oil change, tyres; the distance ones only on vehicles with a reading) are last done so that some are overdue, some due soon and the rest upcoming; the app turns the first two into reminders when the notifications are looked at, as it always does. They are last done where the vehicle's fuel log puts them: a day and the odometer of that day (in the log, on a straight line between two fill-ups; before it, at the log's own pace), or for the tyres an odometer and the day the vehicle got there. The oil change counts both, so it is last done at whichever of the two came later: the app goes by the limit further along, and this way that is the state drawn for it too.

Expenses come in two kinds, all in the currency the vehicle's fill-ups are mostly paid in. Each recurring expense has its past visits as expenses, linked to it as *Mark as done* links them (called like it, in its category, with the odometer when it counts distance): the last one where it was last done, the earlier ones an interval apart, going back three years. Besides those, each vehicle gets one-off expenses (`--expenses`: parking, washing, repairs, accessories) dated within its fuel log, about half of them with the odometer of their day, or over the last half year without one when it has no log. Every expense's reading fits between the readings of the days before and after it, as the app requires. Trashed vehicles keep their one-off expenses (they have no recurring ones).

The access-change notifications are seeded directly for the anonymous user (there is nobody to share with without authentication), over the last two weeks, about made-up people and the first live vehicles: about a third already read, a few standing for several changes, and one "more changes" digest when there are at least ten.

## Screenshots

The README's pictures are in `docs/screenshots/`, one file per page and size (`dashboard-desktop-dark.png`, `home-phone-dark.png`), taken by `mise run screenshots`: it builds `out/` (stamped with the latest tag's version for the footer, or `VERSION=1.2.3 mise run screenshots`; a clone without tags has to set it), seeds a throwaway SQLite database with four vehicles, a couple of years of logs each and a drawn picture each (`--drawn-pictures`, never a downloaded photo: those carry licences of their own), starts the build with sign-in off on port 5110 (`E2E_PORT`; it stops if something already answers there), and runs `src/frontend/tankstat-react/e2e/screenshots.ts` against it (`playwright.screenshots.config.ts`: a phone at two device pixels per CSS pixel and a 1280 × 1024 desktop, dark; the home page and the dashboard in light too; the two dialogs on the phone only). The seed (2026) fixes the vehicles, the amounts and the pictures, but the dates follow the day of the run, so taking them again on another day changes the dates, the overdue days and the monthly figures in every picture. Playwright runs as it does for the real-browser tests: in its Docker image, the reference, where the app's fonts are mapped to Liberation Sans (the image has none of them), or natively with `E2E_NATIVE=1`, where the machine's own fonts show. The page has no service worker (so no "can now be used offline" toast), the notice that sign-in is off and any toast are hidden in the pictures, and each is saved once two frames taken an interval apart are the same. A run without arguments empties the folder first, so the set is always of one run; with Playwright's arguments (`mise run screenshots -- --project desktop-dark`) only those are taken again and the rest keep an earlier run's data, which the script warns about. Take them again by hand when a page they show changes, or the data behind them (the seeder); they are not made in CI, and a change of only the pictures runs no CI.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request that changes more than Markdown files and the README's screenshots (`docs/photo-reading.md`, which a test pins to the code, and Markdown under `tests/` still count; the release workflow always runs it), installing the toolchain from `mise.toml` so CI uses the same Node and .NET versions as developers. Jobs run in parallel: **frontend** (lint, typecheck, the production build of the web app, stamped with the version: `0.0.0-dev.<sha>`, or the release's when the release workflow calls CI), **frontend tests** (Vitest with coverage, in three shards on three machines, split by how long each test file takes as measured in `src/frontend/tankstat-react/tests/durations.json` (`vitest.config.ts`); run `mise run test:durations` again when test files were added or got much slower; Codecov merges their uploads), **backend** (build, then `scripts/test-backend.sh`: the unit test projects and the in-process integration tests side by side, the latter in three processes by the first letter of the test class, since they run one after another within one), **smoke** (the real app as a process in every authentication mode, plus the black-box API contract tests), **codegen** (`schema.graphql` and the generated GraphQL types are up to date). **build** takes the frontend job's web app (no second `vite build`), publishes the API once with the same version (`mise run build:api`) and uploads the self-hostable output (`out/`) as an artifact; it does not wait for the tests, the release does. **e2e** starts that artifact and runs the real-browser tests against it, in two shards on two machines (Chromium, cached per Playwright version); a failure uploads the videos, traces and the report as `e2e-results-1`/`-2`. `node_modules` is cached per lockfile and Node version (`.github/actions/node-modules`), so `npm ci` only runs when the lockfile changed. Run the same things locally with `mise run test` and `mise run test:e2e`.

**Coverage.** The frontend job runs Vitest with V8 coverage (`lcov`) and the backend job runs the unit and integration tests with coverlet (`coverlet.runsettings`: the app's own code, without migrations, the seeder and the tests; Cobertura output). Both upload to [Codecov](https://codecov.io/gh/carathorys/tankstat) under the flags `frontend` and `backend`; `codecov.yml` holds the gates (project coverage may not drop by more than 1% against the base, new and changed lines need 80%) and the pull request comment. The upload needs the Codecov GitHub app and a `CODECOV_TOKEN` repository secret (`release.yml` passes secrets on to `ci.yml`); without the token the step only warns. `mise run test:coverage` writes the same reports to `coverage/` (HTML for the frontend at `coverage/frontend/index.html`).
