# Changelog

Every release of Tankstat, newest first. Each one is a Docker image, `ghcr.io/carathorys/tankstat:<version>`, and a [GitHub release](https://github.com/carathorys/tankstat/releases). The numbers in brackets are the pull requests. Pre-releases (the previews) are not listed: their changes are in the release that followed them.

## 0.19.4 (2026-11-04)

A bugfix release for the toasts.

### Fixed

- **Undo on a toast ran twice on a double tap, then said it had failed** (#243: #255). After a tap on *Undo* the message faded out, but its button stayed on the page, focused, while it went. A second tap, or a second Enter or Space, in that time ran the undo again. The second restore was refused by the server (the entry was no longer in the trash), so right after an Undo that worked the toast said *It could not be put back. Find it in the trash.* The button now runs its action once: once the message is on its way out, a further tap or key does nothing.

### Upgrading

- Nothing to do: no migration and no setting changed. `0.19` and `latest` move to this release.

### For developers

- The real-browser test that trashes a refuelling and takes it back double-taps *Undo* and checks that exactly one `restoreRefueling` request was sent, at both sizes; it failed with two before the fix. Two integration tests (two clicks at once, Enter twice) check the undo runs once, the second one also that both keys reached the button.

## 0.19.3 (2026-11-04)

A bugfix release for dialogs.

### Fixed

- **A dialog taller than the screen scrolled as a whole as well as its content** (#249: #252). In Add widget with *Customize this chart* unfolded, the dialog had a second scrollbar beside its content's, and blank space under the content once that one was scrolled. The visually hidden inputs of the segmented choices (and the text saying why a choice is off) were placed against the dialog's frame instead of its content, so one low in the chart builder stretched the frame. They are now placed within the content in every dialog, and within their own row of choices anywhere else, so only a dialog's content scrolls.

### Upgrading

- Nothing to do: no migration and no setting changed. `0.19` and `latest` move to this release.

### For developers

- The real-browser layout check reports a dialog whose frame scrolls, down or sideways, as `dialog "Add widget": its frame scrolls 998/868`; it runs in every dialog's layout check. The Add widget test failed on it at both sizes before the fix.

## 0.19.2 (2026-11-04)

A bugfix release for the dashboard's charts.

### Fixed

- **A chart that first had no data named every period along its bottom** (#237: #251). A vehicle's dashboard seen while it had no data, logs added, then back to the Dashboard tab: the charts came in without their width measured, so Monthly costs named all 12 months where 6 fit on a desktop and 4 on a phone, overlapping there, until a reload. A chart now measures its picture whenever it is drawn, so it names only the periods that fit.

### Upgrading

- Nothing to do: no migration and no setting changed. `0.19` and `latest` move to this release.

### For developers

- A real-browser test sees a new vehicle's dashboard empty, logs twelve monthly refuellings, comes back to the tab and checks that Monthly costs names fewer than all 12 months, as many as after a reload, at both sizes.

## 0.19.1 (2026-11-04)

A bugfix release for colour contrast.

### Fixed

- **Text on solid colours reaches 4.5:1** (#235: #248). Every red button (the confirmations: move to the trash, sign out, delete a user, restore a backup, delete a chart) and the bell's unread badge were white on a red of 3.91:1, and a hovered primary button in the dark scheme 4.28:1, under WCAG's 4.5:1. Each colour now has a fill chosen for the text on it, at rest and under the pointer: red is a little darker, a primary button looks the same at rest and a little darker under the pointer, in both schemes (the light scheme's hover changes by a shade too). Borders, focus rings and checked controls keep their colours.

### Upgrading

- Nothing to do: no migration and no setting changed. `0.19` and `latest` move to this release.

### For developers

- The real-browser axe scans wait for a dialog or popover they are asked to scan to finish fading in (at most 5 s), scan a hovered primary button and a red confirmation at rest and under the pointer in both schemes, and check the bell badge's fill as drawn (axe skips it, it is `aria-hidden`). The theme's unit test checks every colour's solid fill against its text in both schemes.

## 0.19.0 (2026-11-04)

### Added

- **Dashboard editing, redesigned** (#228: #234). Outside *Customize* a tile shows only its content, and a chart's numbers open as a table in a dialog from the button in its card's header. While customizing, a strip lies across the top of each tile: its grip drags the tile, its ⋮ menu moves, sizes, pins or removes it, and Edit and Delete of one's own saved chart sit on its tile. **One *Add widget* dialog** replaces the old panel, *Add chart* and *Copy as my chart*: the key figures and ready-made charts not on the dashboard, the saved charts not on it and *Blank chart*, each with a live preview; a ready-made chart goes on as it is, or *Customize this chart* makes it one of your own. The chart builder has icon toggles, and what a choice rules out is dimmed with its reason, never hidden. The trend figures' arrows are coloured by meaning (spending, cost per distance and consumption up red, down green; distance neutral); the arrow and the words still say it.
- **A period of its own for each trend figure** (#230: #245). The chip at a trend tile's top right names its period and opens a popover: quick picks, the last so many days, weeks, months, years or fill-ups (weeks, months and years whole if you like), or since a day, compared with the period before or the same dates a year earlier, with a timeline of both and the figure live. Apply keeps it with the dashboard (your own arrangement, else the vehicle's where you may change it, else a new one of your own); *Add widget* offers the same controls. The defaults are the windows the figures always had. The figures' labels are shorter (*Spending*, *Cost per distance change*, *Consumption change*, *Distance driven*), since the chip names the period.
- **Key figures in fewer rows** (#229: #233). A figure shows what its tile's rows hold: two rows its label and value, three its comparison or note as well, four its little chart too. It can be made as low as two rows, except *Spent this month* (three) and the two trend figures whose words need the room (four).

### Changed

- **A chart fits its tile** (#227: #232, #240): its legend and the notes under it are counted, so a tile no longer scrolls or cuts its legend; the resize corner shows only while customizing; a dragged tile follows the pointer instead of freezing until it crosses into another cell.
- **A toast lets a tap through** to the page under it (#239: #241); only its *Undo* takes one, so on a phone it no longer swallows the tap meant for the control at the bottom of the screen.

### Fixed

- **A shared chart offered Edit to someone with Delete access who did not make it**, and the save was refused (#210: #231). The creator edits a chart; the vehicle's owner, an administrator or a Delete grant can delete a shared one, and see only that.
- **Back online, the app caught up only after up to an hour** (#238: #244): an answer that arrived just after going offline counted as the server being back, so the real return refreshed nothing and a change kept on the device waited for the hourly download. A sign-out made offline is told to the server as soon as it can be reached again (#236: #242).

### Upgrading

- **No database migration and no setting changed.** A trend figure's period is stored with its tile in the dashboard's arrangement; an arrangement saved by 0.18 reads as the defaults.
- **The GraphQL schema** gains `VehicleChart.canDelete`, `vehicleTrend` and a tile's `period`; `VehicleChart.canEdit` now means the chart's creator only. A web app of 0.18 still open shows someone with Delete access on someone else's shared chart neither Edit nor Delete until *Reload* on the update notice.
- With sign-in off, Everyone's dashboard offers your private charts, but the server keeps only shared charts in the vehicle's arrangement, so a private chart's tile there does not keep a move, a resize or a removal; share it to keep it there.
- The Docker Compose example now runs 0.19 (`TANKSTAT_VERSION` in `.env.example`).

### For developers

- **Real-browser tests without the service worker** (#236: #242): every file imports `test` and `expect` from `e2e/support/test.ts`, whose context deletes `navigator.serviceWorker`, so the "can now be used offline" toast never lands on a phone test's control; only `offline.e2e.ts` keeps it. Pointer drags run under reduced motion and wait for `settled(locator)`.
- **The test scripts' ports**: the API layer on 5120 (`TANKSTAT_API_PORT`), outside the real-browser tests' 5097-5103; both scripts stop when something already answers on their port (#242).
- **Reachability** counts an answer only for a request sent since the server was last judged out of reach (`connectivity.epoch`, `succeeded(sentIn)`, #244); a jsdom test that navigates at its end waits for its requests with `answered()`, or a failure after the teardown leaves the next test out of reach.
- `DashboardGrid.unit` drives the grid's drag, resize and drop callbacks through a stand-in for react-grid-layout, which jsdom cannot drag (#240).
- The README's screenshots are taken again with the redesigned dashboard.

## 0.18.0 (2026-11-03)

### Added

- **Discounts** (#204: #206, #208, #212, #213). A refuelling keeps the discount the till took off, next to what you paid: a *Discount* field in the dialog, typed or worked out from the litres, the amount paid and the pump's price; *Price per unit before discount* beside the price paid, both as grid columns; and the chart metric *Average fuel price before discounts*. Expenses and *Mark as done* take a discount too. Every sum of spending stays what was paid, and the litres of a refuelling never move by themselves any more: the dialog's rule is in the user guide. Photo reading reads the discount printed for the fuel (and on other receipts), checks the receipt's amounts with it and fills it in; a photo added later keeps the saved values, as before.
- **Telemetry, opt-in** (#214, #219: #216, #221). Traces, metrics and logs over OTLP to a collector of your choice, off until `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The Server settings page gets a *Telemetry* tab (endpoint, protocol, headers as a secret, service name, and `Telemetry__TraceDatabase` for database spans, off by default) and the setup wizard a skippable Telemetry step. What the log never says is never exported either: no GraphQL variables or documents, no query-string values, no user agent, no database parameters, no one-time code, whatever the SDK's own switches are set to. The app's own spans and metrics (`tankstat.*`) cover backups, the restore and the migrations at start, photo reading and sync. Backups record the telemetry settings too, the headers only with `Backups__Secrets`, and a restore on a machine whose environment sets other telemetry settings, or none, is not refused for them, as for the log levels. `docs/configuration.md` has the section.
- **Charts that show where things are heading** (#211: #215, #217). A chart over time can carry a **trend line** (a moving average of three periods, or a linear trend the chart words from its first period to its last), a **running total** and **a year earlier** beside it, all worked out by the server and listed in the chart's table; two new metrics, **cost per distance** and **fuel cost per distance**; and a **second measure** in one chart (distance bars with a consumption line) on a scale of its own. Every currency keeps a scale of its own, a fuel price too.
- **A customizable dashboard** (#211: #218, #223). A vehicle's dashboard is a grid of tiles arranged in place with *Customize*: dragged, resized, pinned, taken off and added back (*Add widget*, by click or drag), with a menu per tile for a keyboard or a screen reader. It can be arranged **for everyone who sees the vehicle** (Edit access to its logs) or **for yourself alone**. Until someone arranges it, the **Standard** preset is the dashboard as it was; **Spending** and **Usage** are two more. Four **trend figures** (spending and cost per distance over the last 12 full months against the 12 before, consumption of the latest fill-ups against the ones before, distance this year against last year to the same day), five more ready-made charts, and **Copy as my chart** on every ready-made one, which opens the chart builder with its recipe.

### Upgrading

- **Four database migrations**, applied at start after the automatic backup that comes before migrations: `AddCostDiscount` (a nullable `Discount` column on `Costs`), `AddChartTrends` and `AddChartSecondMeasure` (five columns on `VehicleCharts`: `Trend`, `RunningTotal`, `CompareYearEarlier`, `SecondKind`, `SecondMetric`), `AddDashboardLayouts` (two tables, `VehicleDashboardLayouts` and `UserDashboardLayouts`).
- **Nothing to set**: telemetry stays off without `OTEL_EXPORTER_OTLP_ENDPOINT`. A setting the SDK could not use (a wrong endpoint, headers naming a key twice, a certificate file that is not there) leaves telemetry off with a Warning naming the setting, never stops the start.
- **A system prompt of your own** (`Recognition__OpenAiCompatible__SystemPrompt` or `SystemPromptFile`): the contract and the JSON schema that go with every photo now ask for the discount whatever the prompt says, and the built-in prompt explains which amount it is; compare yours with the one `docs/photo-reading.md` prints.
- An older web app still open in a browser keeps working: the new chart options and the discount are optional on every input, and an update that leaves the discount out keeps it.
- The Docker Compose example now runs 0.18 (`TANKSTAT_VERSION` in `.env.example`).

### For developers

- **No test host exports telemetry** (#216): `TestApp`, `ApiFixture`, `AppProcess`, the Seeder's tests and the `test-api`, `test-e2e` and `screenshots` scripts drop a developer's `OTEL_*` and `Telemetry__*` before the app starts. `TestSupport/TelemetryKeys` names the keys, `CapturedSpans` and `CapturedMeasurements` find a test's spans and measurements (#221).
- **The app's own spans** (#221): `AppTraces` (an `ActivitySource` named `Tankstat`, `StartJob` for work no request carries, `FailedBy` in an exception filter) and `AppMetrics` (a `Meter` from the host's `IMeterFactory`); the conventions are in `.claude/rules/telemetry.md`.
- **The Seeder** gives a fifth of the fill-ups a discount of one to three per cent, from a random source of its own, so a seed's logs stay as they were (#208).
- **Bar charts are composed from MUI X's parts** (#215), the way `BarChart` itself is, so a line can be drawn over the bars.
- **The date-field tests tell a field's sections by one rule** (#205, #207): the day is the section with a maximum of 28 to 31, since the picker caps it at the month shown; the tests no longer fail in a month of 30 days.
- The README's screenshots are taken again with the dashboard grid and the discount fields.
- Dependency updates: HotChocolate 16.7.3 (#225); MUI X 9.17, motion 14.9, react-router 8.5.1, i18next, lucide-react, the code generator and oxlint (#224).

## 0.17.1 (2026-10-28)

A security and dependency release: nothing in the app changes.

### Security

- **The image's .NET runtime is 10.0.14** (was 10.0.13), with Microsoft's out-of-band fix for **CVE-2026-111497**, an out-of-bounds write in Google's Brotli decoder that .NET bundles (in other ecosystems called "Node2Shell"; [Microsoft's advisory](https://github.com/dotnet/announcements/issues/494)). Tankstat itself decompresses no Brotli: it turns on no request decompression, and its HTTP clients decompress nothing, which are the paths Microsoft names. The fix is in the runtime the image carries, so the image is built again on it; a scanner flags 0.17.0's image for it.

### Changed

- Dependency updates: the .NET packages 10.0.14, HotChocolate 16.7.1, MailKit 4.18.2, the MySQL provider 10.0.12 (#200); MUI X 9.16, Apollo Client 4.3.5, React Router 8.5, motion 14.6, i18next and lucide-react (#199); `fast-uri` and `serialize-javascript`, which only the build uses, for their advisories (#201, #202).

### Upgrading

- Nothing to do in the app: no migration and no setting changed. `0.17` and `latest` move to this release.
- **Running the published build without Docker** (`dotnet out/Tankstat.Api.dll`): the machine's own ASP.NET Core runtime is the one that counts, so update it to 10.0.14 or later.

## 0.17.0 (2026-10-21)

### Added

- **Passwords done properly** (#151, #152, #155, #156, #157, #158):
  - one password field everywhere (sign-in, password changes, the server's secrets, backup passphrases), each with its own **Show** button that hides again when you leave the field, send the form or stop typing for 30 seconds, and a note while **Caps Lock** is on;
  - **every new password is typed twice**: a change, a reset or first-password link, one an administrator sets, and the setup's first administrator; the form and the server both check the repetition;
  - **a minimum length you set**: *Minimum password length* in the sign-in settings (`Auth__Standalone__PasswordMinLength`, 10 to 128, 10 by default), which applies at once, without a restart; every new-password field says it before you type;
  - **a new password asked for at the next sign-in** when the minimum was raised above a user's password: they sign in, nobody is signed out, and the app shows only *Choose a new password* until it is changed;
  - **a strength meter** under every new password, with what makes it weak (a common password, your own name or address, repeats, runs, keyboard rows); advice only;
  - **backup passphrases** typed twice too, a made-up one typed back before *Use these keys for new backups* turns on, a minimum of their own (`Backups__PassphraseMinLength`, 12 to 128, 12 by default), and the backup commands ask for a passphrase without showing it at a terminal.
- **Signing out works without the server** (#163): the sign-in screen shows at once, offline too. The device tells the server once it can be reached, stays signed out until then, and remembers only that it signed out, never a secret. With an identity provider, *Sign in* goes to the provider only once the server has ended the old session.

### Changed

- **Every sign-out deletes what the device keeps for the account** (#163): the downloaded data, the pictures, the changes not sent yet and the entries found after a restore of the server. When the device holds changes or found entries the server does not have, it says how many and asks first; *Cancel* keeps everything.
- **The setup page is the only way to a Standalone instance's first administrator** (#165): `Auth__Standalone__AdminEmail` and `Auth__Standalone__AdminPassword` are gone. A deployment without a browser finishes the setup with the one-time code from the log and one request (`docs/authentication.md`). A set-up Standalone instance left without an administrator (a switch to Standalone outside the app, every administrator gone) opens its setup page again at start, with a new code, instead of failing to start; until it is finished, local users who are not administrators cannot sign in.
- **Charts of costs give each currency a scale of its own** (#164), so a currency with small amounts is no longer a sliver beside a large one; with several, the line under the chart says so, and the amounts are in the tooltip and the table (a month without spending in a currency shows a dash). A month's bars are only its own currencies, centred, and the month labels are shorter: the month alone, its year on a second line under the first month and every January.
- **Mark as done starts with only the opened schedule's category ticked** (#166): opened from an oil change in *Service*, the visit starts with it and the other due *Service* schedules, not the vignette in *Fees*. Categories match ignoring spaces at either end and capitals, but not accents; a schedule without a category starts alone; the expense's prefilled title and category follow what is ticked. On a vehicle's *Recurring* tab and a home card's Done alike.

### Fixed

- **Two .NET denial-of-service advisories in a dependency** (CVE-2026-84008, high, and CVE-2026-70162, moderate; #196): MailKit's MimeKit asked for `System.Security.Cryptography.Pkcs` 10.0.0, affected by both, which 0.16.0 shipped too; it is pinned to the fixed 10.0.13. The runtime packages are at .NET 10.0.13 and HotChocolate 16.6.9 (#183).

### Upgrading

- **One database migration**, `AddPasswordChangeRequired` (a column on the users), applied at start after the automatic backup that comes before migrations.
- **Remove `Auth__Standalone__AdminEmail` and `Auth__Standalone__AdminPassword`**: they are ignored now, with a Warning at start that names them (never their values). A new Standalone instance makes its first administrator on the setup page.
- **`Setup__Enabled=false` with Standalone and no enabled administrator stops the start**; the message says how to go on.
- **Scripts that pipe a passphrase into `backup keygen` or `backup --passphrase-stdin` need two lines now**, the passphrase and its repetition: `printf '%s\n%s\n' "$P" "$P" | docker exec -i tankstat dotnet Tankstat.Api.dll backup keygen --passphrase-stdin`. Given one line they stop without writing anything. `decrypt` and `restore` still take one.
- **Signing out deletes the account's offline data on that device**, changes not sent yet included (after a warning): sync or keep what you need before signing out.
- If you use no categories on recurring expenses, each Done now starts with only its own schedule ticked; tick the others by hand.
- Nothing changes for existing passwords until an administrator raises the minimum.
- The Docker Compose example now runs 0.17 (`TANKSTAT_VERSION` in `.env.example`).

### For developers

- **The web app moved to `src/frontend/tankstat-react`** (#168): one npm package with its own `package.json`, `node_modules` and configs; `mise run install` runs `npm ci` there.
- **The Seeder** writes expenses, each schedule's visits linked as *Mark as done* links them and one-off ones (#167), places every reading on the vehicle's odometer trajectory, and draws a picture per vehicle with `--drawn-pictures` (or fetches its model's photo from Wikimedia Commons with `--pictures`, for a local database only) (#171).
- **Screenshots for the README** (#170, #181): `mise run screenshots` builds `out/`, seeds a throwaway database with drawn pictures and takes them with Playwright at phone and desktop size.
- **Community files** (#169): `CONTRIBUTING.md`, `SECURITY.md` (private vulnerability reports), a code of conduct, issue forms, a pull request template, Dependabot (weekly, grouped), and `AGENTS.md` for other coding agents. The Claude Code GitHub workflows are gone (#180, #182).
- **msw 3** (#195): the tests' GraphQL handlers come from `tests/support/graphql.ts` (msw 3 intercepts GraphQL only through a link), and `answered()` in `tests/support/server.ts` waits for every request in flight before a test cuts the connection.
- Dependency updates: Apollo Client 4.3.4, MUI 9.5, React 19.3.1, TypeScript 7.0, Vite 8.3.5, motion 14.3, the GitHub Actions, the test SDKs and the GraphQL code generator (#172–#179, #183–#187).

## 0.16.0 (2026-10-15)

### Added

- **A setup page for a new instance** (#130, #131, #132, #139): a new instance with no administrator waits for its setup instead of stopping. Its log names a one-time code (the line starting "Setup is waiting"); with it, the setup page at the server's address opens one wizard:
  - the Server settings page's groups, a step each, the sign-in mode first and chosen on purpose;
  - then the gatekeeper: the first administrator in Standalone, the acknowledgement that everyone sees and changes everything with sign-in off, or in OIDC and proxy mode the administrators named in `Auth:AdminEmails` or the first person who signs in and claims it;
  - one restart at the end when a saved setting waits for it, then a done screen with the backups' key set and what to set up next.

  *Restore from a backup* instead lists the backups folder and takes an upload, so moving a server to a new machine starts on its setup page. The setup is skipped where the environment already decides it: the bootstrap administrator, `Auth:AdminEmails`, or `Setup__Enabled=false`. An instance that holds data counts as set up.
- **The settings file in every backup** (#143): a backup carries the app's own settings file (the instance's settings only, never the machine's: no database, folders or keys folder), and a restore brings it back, keeping this server's previous file as `settings.json.bak`. A restore is judged by the settings it would leave, so a moved Standalone server can no longer come up with sign-in off: a file this server would not start with is refused before anything changes.
- **The environment's settings recorded or merged** (#144): `Backups:Environment` (*Settings from the environment* on the Server settings page). *Names only* (the default) names in the backup the settings its server's environment variables held, never a value; *Names and values* also puts their values into the backup's copy of the settings file (never into the server's own). `backup --environment record|merge` chooses for one run.
- **A restore is refused while a recorded setting has no value here** (#144): neither this server's environment nor the restored settings file holds it. The refusal names the variables: set them, then restore. A log level (the `Logging` section) is the exception: it never refuses a restore (#148).
- **What a restore can break, said before it** (#146, #148): each backup's row on the Backups page and the setup page says what refuses its restore here ("Refused") and what it risks ("Risk": no settings file, secrets left out, settings this server's environment overrides, the sign-in mode above all, log levels this server lacks). The restore dialog says each in full, and a risk must be acknowledged before *Restore and restart*. The `restore` command prints the same.
- **Secrets in backups by choice** (#148): `Backups:Secrets` (*Secrets in backups*) puts the OIDC client secret, the SMTP password and the model API key into the backup's settings file; off by default, with a warning on the Server settings page while backups are not encrypted. A backup always names the secrets it leaves out. `backup --secrets` for one run.
- **Hold a backup** (#148): a held backup is never removed by retention and cannot be deleted until it is released (*Hold* / *Release* on the Backups page, or a `<file>.hold` file beside it with sign-in off).
- **Open a backup** (#148): a dialog per backup, on the Backups page and the setup page, with what its manifest says: release, database and migration, encryption, what it holds, every table with its rows, its settings file and the variables its server's environment set.

### Changed

- **A plain backup that holds its secrets is marked** with a red open padlock on the Backups page (#148).
- **A value the server refuses is said at its field** (#139), like the form's own checks, everywhere: the log, vehicle, schedule, visit and chart dialogs, the restore and key-set dialogs, and the Server settings page.
- **The one-time code** for a restore with sign-in off is now the instance's setup code, 12 characters shown `XXXX-XXXX-XXXX` (#130).

### Fixed

- **Two requests that create the same user at once** (#145): a proxy user's first page, whose requests come together, or an administrator's *Create user* sent twice. One creates the user and the other reads it; before, the loser ended with an error (a 500 through a proxy). An administrator another server made at the same moment now completes the setup.

### Upgrading

- One migration, `AddSetup`, applied at start; as with every migration, the start writes a backup of the database first. An instance that holds data counts as set up: it shows no setup page.
- **A new instance with nothing configured waits for its setup instead of stopping**: in Standalone without the bootstrap administrator, its address shows the setup page. An unattended deployment that should never see it sets the bootstrap administrator (as before), `Auth:AdminEmails` in OIDC or proxy mode, or `Setup__Enabled=false`.
- **Backups now hold the settings file, and restores check the settings**: a backup of 0.16 restored on a server without the variables its server set is refused until they are set here (the refusal and the Backups page name them). With *Names and values* the backup carries their values instead.
- **No backup holds the secrets unless `Backups:Secrets` is on**, encrypted or not. A restore keeps this server's own secrets where the backup has none. Backups written by 0.15 restore as before and bring no settings file.
- The Docker Compose example now runs 0.16 (`TANKSTAT_VERSION` in `.env.example`), and its administrator variables may be left empty to use the setup page.

### For developers

- **A fourth test layer: real-browser tests** (#135, #136, #137): Playwright against the published build at phone and desktop size (`mise run test:e2e`, CI's `e2e` job in two shards): a layout check (no sideways scroll, values inside their panels, 44 px touch targets), axe with the real styles, and the user flows by area, each as a user of its own, with a video of a failed test. Every new user flow gets a Playwright test (#141 steadied a flaky one).
- The issue workflow's skills use the main checkout, with no git worktree unless asked; CI does not run for a change of Markdown files only (#142).
- `SettingsInBackups.CategoryOf` sorts every setting the app binds as the instance's, a secret or the machine's, and a test fails for one left unsorted (#148).

## 0.15.1 (2026-10-14)

### Fixed

- **Label and value lists on a phone** (#127): on a narrow screen each label now stands above its value, and a value wraps instead of running past its panel. Before, the Server settings page squeezed the environment's settings into a one-word column, long addresses and ids ran off the screen under their help buttons, and a long settings-file path made the whole page scroll sideways. On wider screens the label column takes at most 40 %. This covers every such list: Server settings, Overview, Backups and Account.
- **Touch targets of 44 px on a touch screen** (#128): the app's name in the top bar, and every text link that is not part of a sentence. That means the Overview's user and vehicle counts, the vehicle names under Administration, then Vehicles, the Backups page's link to its settings, a chart's *Show the data as a table*, a sync conflict's *Open*, *Continue to sign in* after setting a password, and a notification's link. Nothing changes with a mouse.
- **The Docker Compose example runs 0.15** (#126): `.env.example` said `0.14`, which ignores the example's backup settings without a word. Its comments now name the tags that exist (`0.15` follows the patches, `0.15.0` stays put, `latest` moves with every stable release) and say that the example's backups need 0.15 or later.

### Upgrading

- Nothing to do: no migration and no setting changed. `0.15` and `latest` move to this release.
- If you started from the Compose example with `TANKSTAT_VERSION=0.14`, read 0.15.0's *Upgrading* below, then set it to `0.15`: the example's backups only run from 0.15.

### For developers

- `.claude/rules/frontend.md` has a *Touch targets* rule: `standaloneLink` (`theme/components.ts`) for a link that stands on its own, and links in a sentence are exempt (#128).
- `.claude/rules/docker-and-release.md` says which image tags each release gets, and that a new minor release's changelog PR also moves the Compose example's `TANKSTAT_VERSION` (#126).

## 0.15.0 (2026-10-13)

### Added

- **Backups in one file** (#107): every table, the pictures, the photos of logs (when they live with the pictures, or with `Backups:Photos` when they have a folder of their own) and, in an encrypted backup, the keys that keep people signed in. Encryption is the `age` format exactly, so a backup opens with `age -d` too; the server keeps only the recipient, never a key that opens its own backups. Commands next to the server: `backup`, `backup keygen` and `decrypt`. The image writes them to `/data/backups`.
- **Restore a backup** from the app or with `tankstat restore` (#110, #118): the server restarts in its own process and applies it first thing at that start, before the database migrations, replacing the data as a whole. A backup moves between SQLite, PostgreSQL, SQL Server and MySQL, and one made by an older release is brought up to date as an upgrade would be; a newer release's is refused before anything is written. Devices with offline data notice the restore, download afresh, and list what the server no longer has under *Found on this device* on Waiting to sync, to send again or forget.
- **A Backups page** for administrators (Administration, then Backups) (#112, #118): back up now, upload, download, restore and delete backups, see each one's kind, release, rows, encryption, photos and size, and make a key set for encryption.
- **Scheduled backups and retention** (#113, #118): `Backups:Every` (one or more times of day, the server's time) on the days of `Backups:Days`, with `Backups:Keep` and `Backups:KeepDays` removing the old ones. A backup missed while the app was down is written at its next start, and a failed one tells the administrators, once a day at most.
- **A backup before every upgrade** (#118): a start that migrates a database holding data first writes a backup of it as it is (a safety net).
- **The backup settings on the Server settings page**, applied at once; *Make a key set* saves its recipient and salt there with one button (#117).
- **Time and date formats per user** (#118): the top bar's *Language and formats* menu offers a 24-hour or 12-hour clock and five date formats, kept with the account. *Default* follows the new `Defaults:TimeFormat` and `Defaults:DateFormat`, else the language.
- **The server's folders on the administration overview** (pictures, photos of logs, backups, sign-in keys, the settings file), each with what it holds and its environment variable (#118).
- **A Docker Compose example** with PostgreSQL and Mailpit, and an `.env.example` (#119).

### Changed

- The Server settings page words its choices (the value after them), warns that no sign-in is not secure, and the *Defaults for new vehicles* tab is now *Defaults* (#118).

### Upgrading

- Two migrations, `AddInstanceGeneration` and `AddUiFormats`, applied at start. **That start writes a backup of the database first**, as every start that migrates a database holding data now does; when it cannot be written, nothing is migrated and the start stops with the reason in the log. Keep the backups folder writable (`/data/backups` in the image, created for the app's user) and leave room for one more backup.
- `Backups:Path` must not be a storage folder, lie inside one or hold one; the start refuses that. With the image's default `/data/backups`, a `Storage__Path=/data` needs a `Backups__Path` outside `/data` or a narrower storage folder.
- **A restore keeps no copy of the data it replaces**: whatever changed since the restored backup was made is gone. Download or copy a backup of the current state first if you might want it back.
- Backups are plain until a key set is made (the Backups page, or `backup keygen`); a plain backup leaves the sign-in keys out, so restoring one signs everyone out once.
- Keep a copy of the app's own settings file (`/data/settings.json`) with your backups: the backup settings saved on the page live there, and no backup holds it.

### For developers

- `CHANGELOG.md`, carried by every release (#115).
- The release smoke test checks that a setting of a group that applies at once counts at once, and that a sign-in setting waits for the restart (#116).
- `CLAUDE.md` keeps what holds everywhere plus an index; each area's rules are in `.claude/rules/<area>.md`, loaded when a file of the area is read (#118).
- Backup fixtures: files written by `age` 1.3.2, and the committed backup of an older migration that every release must restore; `ProviderRoundTripTests` moves a backup between real servers when given their connection strings (#107, #110, #118).

## 0.14.0 (2026-10-12)

### Added

- **Server settings for administrators** (Administration, then Server settings): every setting with where its value comes from (the environment, the settings file, `appsettings.json` or the built-in default), a tab per group, and changes saved into the app's own settings file. Defaults, notifications, offline sync and logging apply at once; sign-in, e-mail and photo reading after *Restart server*, which restarts the server in its own process, so the container keeps running. A setting the environment holds is read-only there, and a secret is never shown back (#106, #108, #114).
- **The app's own settings file**, `Settings:Path` (`/data/settings.json` in the image): a configuration source below the environment variables and above `appsettings.json`. A file the app could not start with is left out at start, with an `Error` line naming the settings at fault and a public notice (#105).
- **An Administration group** in the navigation: an Overview of what needs an administrator, People (users and access), Vehicles (the full list, moved out of the main navigation) and Server settings (#114).
- **The photos of logs under a root of their own**, `Storage:PhotosPath` (opt-in; nothing moves unless it is set) (#95).

### Changed

- A start in another sign-in mode than the previous one signs every device out (#106).
- Self-service password resets are throttled, with one live link per user (#94), and their e-mail goes out after the answer, so the answer tells no accounts apart (#104).
- `Smtp:Security` takes MailKit's names: `Auto`, `None`, `StartTls`, `StartTlsWhenAvailable`, `SslOnConnect`. The `Ssl` the docs used to list was never one of them and runs as `Auto`, as it always did (#105).

### Fixed

- In mode `None`, a sign-in cookie left over from another mode no longer names a user (#98).
- An administrator keeps the setup link to hand over when its e-mail cannot be sent (#99).

### Upgrading

- Nothing to do: the image reads `/data/settings.json` from the data volume, and the file appears with the first change saved on the Server settings page. Environment variables still win over it.
- A settings file that would leave the instance without sign-in when left out stops the start instead: that is a file that sets the sign-in mode while the environment does not, over the shipped `None`. Fix the file, or set `Auth__Mode` in the environment.
- For TLS from the first byte to the mail server, set `Smtp__Security=SslOnConnect`.
- The old addresses keep working: `/admin?tab=users` and `?tab=access` lead to People, `/vehicles` to Administration, then Vehicles.

### For developers

- The review skill: a PR is claimed with `reviewing` and labelled `reviewed` when done (#101).
- A log canary ignores a needle that a random id spells by chance (#102).
- Code lookups go through the LSP (#103).

## 0.13.0 (2026-10-09)

- Let each user choose how surfaces are drawn: glossy, transparent or opaque (toasts readable again) (#70)
- Keep photos kept on the device side by side, marked on the thumbnail, and say they were kept, not uploaded (#71)
- Show the web app's version in the footer, also while the server is out of reach (#74)
- Collect the integration tests' coverage again (#73)
- Raise test coverage: frontend 89 % to 95.7 %, backend 93.4 % to 99 % (#75)
- Conflicts on sync: merge edits, and say plainly what happened on each side of a trash or delete (#77)
- Log a null in a non-null GraphQL field as a Warning, naming the field by the schema (#79)
- Keep pictures offline for use without the server (#76)
- Add the triage and dev skills: one issue workflow for every agent (#82)
- Add service worker registration and chunk recovery for PWA (#83)
- Clean an invalid browser language (en-US@posix) to a valid tag, so no page crashes formatting a number (#81)
- Skills: triage claims issues with `triaging`; dev works in a git worktree per task off the latest main (#84)
- Dev skill: claim an issue with the `in progress` label, refuse one another agent has claimed (#86)
- Change a vehicle's picture offline: keep it on the device and send it when the device syncs (#87)

## 0.12.1 (2026-10-08)

- Balance the CI test jobs: integration tests in three processes, frontend shards by measured time (#59)
- Keep the top bar within a phone's width, so the page no longer scrolls sideways (#60)
- Show a row's sync state in a narrow column of the lists, with a coloured bubble that says why (#61)

## 0.12.0 (2026-10-08)

- Split the README into docs/ pages and keep it short (#34)
- Add the caveman rule files for the other coding agents (#52)
- Report the released version, pre-release label included, in health (#56)
- Build once and reuse it: faster CI, release image from CI's build, steadier frontend tests (#57)
- Offline sync 1/16: client-chosen ids and entity versions (#37)
- Offline sync 2/16: access and refresh tokens (#38)
- Offline sync 3/16: signed-in devices on the Account page (#39)
- Offline sync 4/16: know when the server is out of reach (#42)
- Offline sync 5/16: keep what was opened on the device (#43)
- Offline sync 6/16: the server's download side (feed, tombstones, offline window) (#44)
- Offline sync 7/16: download the offline window, and answer the log lists from it offline (#46)
- Offline sync 8/16: choose what is downloaded for offline use (Account page) (#47)
- Offline sync 9/16: keep changes of refuellings and expenses on the device (#48)
- Offline sync 10/16: keep changes of vehicles and recurring expenses on the device (#49)
- Offline sync 11/16: the Waiting to sync page, Keep, and asking before signing out (#50)
- Offline sync 12/16: send the changes kept on the device (syncChanges, ledger, conditional writes) (#51)
- Offline sync 13/16: decide about the changes the server could not apply (parked changes) (#53)
- Offline sync 14/16: photos offline (kept on the device, sent right before their entries) (#54)
- Offline sync 15/16: edit a waiting or parked change, one tab syncs, offline docs (#55)
- Offline sync 16/16: fixes from trying it out (a new vehicle offline, the vehicle list offline) (#58)

## 0.11.0 (2026-10-07)

- Notify about a new version and let the user reload (#33)

## 0.10.1 (2026-10-07)

- Fix: the chart hover tooltip showed nothing (it was placed outside the screen) (#32)

## 0.10.0 (2026-10-07)

- Move the frontend from Radix to MUI v9 and MUI X, with a saved colour mode (#31)

## 0.9.1 (2026-10-06)

- Fix: a full page of logs fails on canDelete (one query per row in parallel) (#30)

## 0.9.0 (2026-10-06)

- Mark several recurring schedules done at one service visit, with one optional amount (#23)
- Organize the frontend tests into folders by feature area (#24)
- Add the Claude GitHub actions (#26)
- Read photos added while editing a log, and let an emptied value wait for them (#28)
- More motion and clearer waiting: progress near Save, busy buttons, rows that leave and cards that move (#29)
- Show spending in every currency, never store a guessed currency, keep the price column's currency (#27)

## 0.8.0 (2026-10-06)

- Add a unit price to the refuelling dialog: any two amounts give the third (#17)
- Let a refuelling say a fill-up before it was not logged (#18)
- Log a refuelling, an expense or a finished schedule straight from the home page (#21)
- Install as an app, and sign in with OIDC in one hop (#19)
- Keep the UI settings with the account, and let the user arrange their vehicles (#20)
- Fix flaky first tests in the frontend CI job (#22)

## 0.7.1 (2026-10-05)

- Say why a photo gave nothing: reasons, a trail in the log, and the whole request in the README (#14)
- Log what is sent to the model and what it answers, on request (#15)
- Tell the user why a photo gave less than it might have (#16)

## 0.7.0 (2026-10-05)

- Read photos with a vision model behind any OpenAI-compatible API, in place of the Tesseract reader (#13)

## 0.6.0 (2026-10-03)

- Log what happens in the app, so an operator can follow it (#12)
- Read the odometer of a dashboard the OCR mistook for a receipt (#11)

## 0.5.0 (2026-10-03)

- Upload photos as soon as they are picked (photo drafts) (#8)
- Read odometers and receipts from photos (standalone reader service) (#9)
- Fill the log dialogs from photos read on the server (#10)

## 0.4.0 (2026-10-02)

- Fix Codecov reporting only the frontend coverage (#6)
- Notifications (access changes, due recurring expenses) and configurable reminder defaults (#7)

## 0.3.0 (2026-10-02)

- Recurring expenses, admin-only vehicle list, searchable home page (#4)
- Import repeating Fuelio reminders as recurring expenses (#5)

## 0.2.0 (2026-10-02)

- Administrator user management: edit, set password, delete with data move (#1)
- Add code coverage reporting and status badges (#3)
- Photos for refuelings and expenses, uploads stored per vehicle (#2)

## 0.1.0 (2026-10-01)

- Refuelling logs, sharing, delete levels and pictures
- Expenses, Fuelio import and stored fuel consumption
- The welcome screen, the vehicle dashboard and composable charts
- Lazy-loaded pages and charts; vehicle cards that slide up on CSS backgrounds
- A fixed mobile layout: overflow, compact grid actions, the drawer's header

## 0.0.1 (2026-10-01)

- The first release: a React and .NET 10 GraphQL app with vehicles, the authentication modes, access control, server-side grids, i18n, generated GraphQL types, a test-data seeder, smoke tests, CI and the single-container Docker image on GHCR
