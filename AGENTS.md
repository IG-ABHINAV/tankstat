# Agents

Guidance for coding agents (Cursor, Copilot, Cline, Windsurf, OpenCode, Codex and the like) working on a contribution to this repository. Read the files named here before changing anything; this one only says where the conventions are and which of them apply to you.

## The project's conventions: follow them

- [CLAUDE.md](CLAUDE.md), from *Toolchain and commands* on: the stack, the layout and layers, the commands (`mise run <task>`), the testing requirements, the frontend rules (accessibility, i18n in English and Hungarian, GraphQL codegen), migrations for every provider, the scope. It is written for Claude Code, but these sections are the project's rules for everyone.
- [`.claude/rules/`](.claude/rules/), one file per area (fuel log and statistics, expenses and imports, notifications, photos, offline sync, backups, photo reading, the settings file, authentication, setup, UI settings, frontend, logging, PWA, CI and tests, real-browser tests, Docker and release, the seeder). Read the file of the area a change touches before starting, and update it when the change alters what it says.
- [`docs/`](docs/): the documentation for users and operators, one file per topic; a change that alters behaviour or a setting updates the matching file.
- Run `mise run test` before finishing a change; a new or changed user flow also needs a real-browser test (`mise run test:e2e`).

## The issue workflow skills: the maintainer's, in any agent

`triage`, `dev` and `review` are [Agent Skills](https://agentskills.io): `SKILL.md` files in `.agents/skills/`, a symlink to `.claude/skills/` (the same files, so Claude Code and the other agents follow one copy). They are the maintainer's issue workflow, and each says in *Tools in any agent* what to use in place of the Claude Code tools it names. Edit them in `.claude/skills/`.

- **In the maintainer's sessions** (any agent): follow them, labels included.
- **In a contribution:** don't run them, and never set or remove their labels (`triaging`, `triaged`, `in progress`, `reviewing`, `reviewed`, `WIP`); say on the issue that you are working on it.

## The maintainer's own sessions: not for you

CLAUDE.md also holds rules for the maintainer's Claude Code sessions. They do not apply to a contribution:

- the issue workflow and its labels, as above;
- one agent at a time in the main checkout, no git worktree, the LSP tool: work however your setup works;
- the lines a pull request or commit ends with ("Generated with Claude Code", the session link): use your own tool's, or none.

[CONTRIBUTING.md](CONTRIBUTING.md) says what a pull request needs and how it is reviewed.
