# Tankstat

[![CI](https://github.com/carathorys/tankstat/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/carathorys/tankstat/actions/workflows/ci.yml)
[![Release workflow](https://github.com/carathorys/tankstat/actions/workflows/release.yml/badge.svg)](https://github.com/carathorys/tankstat/actions/workflows/release.yml)
[![codecov](https://codecov.io/gh/carathorys/tankstat/graph/badge.svg)](https://codecov.io/gh/carathorys/tankstat)
[![Latest release](https://img.shields.io/github/v/release/carathorys/tankstat?include_prereleases&sort=semver)](https://github.com/carathorys/tankstat/releases)
[![License: MIT](https://img.shields.io/github/license/carathorys/tankstat)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Node LTS](https://img.shields.io/badge/Node-LTS-339933?logo=nodedotjs&logoColor=white)
![TypeScript](https://img.shields.io/badge/TypeScript-3178C6?logo=typescript&logoColor=white)

A self-hosted fuel log for your vehicles: refuelings, other costs, recurring expenses and what they add up to, for you and the people you share a car with. One container is the whole service, and it installs as an app on phones and desktops.

Under the hood: a React + TypeScript frontend (Vite, MUI) and a .NET 10 backend that talk GraphQL (HotChocolate, Apollo Client), with Entity Framework Core for the database. Kestrel serves both the API and the built frontend.

**Status:** `0.x`, released often: read a release's notes in the [CHANGELOG](CHANGELOG.md) before upgrading (from 0.14 on, each has an *Upgrading* section), and see [Self-hosting](docs/self-hosting.md) for the image tags and [Backup and restore](docs/backup-and-restore.md) for the backup an upgrade writes first. 1.0 is when the data model and the settings have stopped moving.

<p align="center">
  <img src="docs/screenshots/dashboard-desktop-dark.png" alt="A vehicle's dashboard on a desktop: the key figures (spent this month, average consumption, odometer, last fill-up) and the Overview charts, monthly costs per currency and consumption." width="100%">
</p>
<p align="center">
  <img src="docs/screenshots/home-phone-dark.png" alt="The home page on a phone: a card per vehicle over its picture, with its overdue reminders, each with a Done button, and the Refuel and Expense buttons." width="32%">
  <img src="docs/screenshots/dashboard-phone-dark.png" alt="A vehicle's dashboard on a phone: its picture and name, the tabs, and the key figures below them." width="32%">
  <img src="docs/screenshots/add-refuelling-phone-dark.png" alt="The Add refuelling dialog on a phone: date, volume, price, total cost, odometer with the last reading, full tank, note and photos." width="32%">
</p>

Recurring expenses: a schedule by time, by distance or whichever comes first, its state on the home card and on the vehicle's Recurring tab, and a service visit that marks several done at once, with an optional amount logged as one expense.

<p align="center">
  <img src="docs/screenshots/recurring-desktop-dark.png" alt="A vehicle's Recurring tab on a desktop: each schedule with how it repeats, when it was last done, when it is next due, and its state, overdue or upcoming, with a gauge for time and one for distance." width="100%">
</p>
<p align="center">
  <img src="docs/screenshots/recurring-phone-dark.png" alt="The Recurring tab on a phone." width="32%">
  <img src="docs/screenshots/mark-as-done-phone-dark.png" alt="The Mark as done dialog on a phone: the vehicle's schedules as checkboxes with the ones done at this visit ticked, the date and the odometer, and an optional amount logged as one expense." width="32%">
</p>

More in [`docs/screenshots/`](docs/screenshots/): the refuelling grid and the light scheme.

## Features

- **Vehicles and refuelings**: per-vehicle units and currencies (never converted), odometer checks, full and partial fill-ups, and the fuel consumption between full fill-ups.
- **Expenses**: service, insurance, parking and anything else, with a free-text category and an optional odometer.
- **Recurring expenses**: schedules by time, by distance or whichever comes first, with *due soon* and *overdue* reminders; a service visit marks several of them done at once.
- **Dashboard and charts**: key figures, ready-made charts and your own, arranged per vehicle or for yourself alone (the Standard preset until then) (metric, grouping, chart type, period, a trend line, a running total or a year earlier beside it, a second measure on a scale of its own; cost per distance among the metrics), costs always per currency.
- **Photos and photo reading**: up to ten photos per entry, and optionally a vision model behind any OpenAI-compatible API that reads the odometer or the receipt into the form. Off by default; nothing leaves your server unless you point it at a paid API.
- **Notifications**: access changes and due schedules, folded so nobody is flooded.
- **Import** from Fuelio (CSV), with a preview and duplicate handling.
- **Sharing and access control**: owners, administrators, instance-wide defaults, per-user grants, and sharing a single vehicle's logs.
- **Sign-in your way**: none, the app's own accounts, OpenID Connect, or a trusted reverse proxy (Authelia, Authentik, oauth2-proxy, ...).
- **Your database**: SQLite (no setup), PostgreSQL, SQL Server or MySQL.
- **Backup and restore**: one file with every table and the pictures, written every night at a time you set, from the administration page (which also uploads, downloads and restores them) or by a command next to the server, with old ones removed by count or age; once a passphrase is set up it is encrypted and holds the keys too, readable with `age` without Tankstat; a restore takes the whole instance back to it at the next start, and devices with offline data follow.
- **Installable web app** that works while the server is out of reach: your entries of a time window you choose are downloaded to the device and kept current, what you add, change or remove (photos included) is kept on the device and synced when the server is back, and changes the server could not apply wait for someone who may change the vehicle to apply or discard them. An update prompt never reloads on its own.
- **Follows you**: the sidebar, grid columns, language, colour mode and the order of your vehicles are saved with your account.
- **English and Hungarian**, dark and light mode, built for keyboards, screen readers and reduced motion.

## Quick start

```sh
docker run -d --name tankstat -p 8080:8080 -v tankstat-data:/data \
  -e Auth__Mode=Standalone \
  ghcr.io/carathorys/tankstat:latest
```

Then open http://localhost:8080: the first visit shows a setup wizard, which takes you through the settings and makes the first administrator (or restores a backup) with the one-time code from `docker logs tankstat`. A script can finish it without a browser, see [Authentication](docs/authentication.md#the-first-start-setup). The `/data` volume holds the SQLite database and the uploaded pictures. To build the image yourself instead, run `mise run docker:build` and use `tankstat:local` as the image name.

> **Always set `Auth__Mode`.** The shipped `appsettings.json` runs without authentication: everyone sees and changes everything, and there is no administrator. Put TLS in front of the container, and never commit real credentials.

Everything is configured through environment variables, or by an administrator on the *Server settings* page once it runs (the app's own settings file, below the environment), see [Configuration](docs/configuration.md) (the other databases included) and [Authentication](docs/authentication.md). Running without Docker and the release images are in [Self-hosting](docs/self-hosting.md).

## Documentation

- [User guide](docs/user-guide.md): what is on the screen and how each feature behaves.
- [Importing](docs/importing.md): bringing your logs from Fuelio.
- [Install as an app](docs/install-as-an-app.md): the home screen, offline use and updates.
- [Configuration](docs/configuration.md): database, uploaded files, defaults, notifications, the app's own settings file and logging.
- [Authentication and access control](docs/authentication.md): the four sign-in modes, every setting, and who may see what.
- [Reading photos with an OpenAI-compatible model](docs/photo-reading.md): setting up a vision model, what it is told, and following a photo through the log.
- [Backup and restore](docs/backup-and-restore.md): what is in a backup, encrypting it with a passphrase, opening it again, restoring it.
- [Self-hosting](docs/self-hosting.md): the Docker image, running the published build, releases.
- [Development](docs/development.md): toolchain, layout, running, testing, migrations, test data, CI.

## Development

[mise](https://mise.jdx.dev) installs the toolchain (Node LTS and .NET 10):

```sh
mise install && mise run install   # toolchain, npm ci, dotnet restore
mise run dev:api                   # API with hot reload on http://localhost:5080
mise run dev:web                   # Vite dev server, proxying to the API
mise run test                      # typecheck, lint, every test layer
```

See [Development](docs/development.md) for the layout, the test layers and the tools around them.

## Contributing

Bug reports, questions and pull requests are welcome: [CONTRIBUTING.md](CONTRIBUTING.md) says what a change needs and how work is tracked here (the labels you will see in the issues), [Discussions](https://github.com/carathorys/tankstat/discussions) take questions and ideas, and issues labelled [`good first issue`](https://github.com/carathorys/tankstat/labels/good%20first%20issue) are picked out as a place to start, when there are some. Security problems go through [SECURITY.md](SECURITY.md), privately.

## License

[MIT](LICENSE).
