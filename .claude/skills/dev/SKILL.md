---
name: dev
description: Implement a triaged GitHub issue - refuse one that is not `triaged`, never start one that is `in progress` unless the user asks for it specifically (then continue the ongoing work or restart it, asking when unsure), claim it with the `in progress` label, consult the advisor before every decision, ask the user when a question comes up (with a clickable mockup or diagrams, as in triage), read its handoff and links, work in the main checkout on a branch off the freshly pulled main (no git worktree unless the user asks for one), only the affected tests (none for a Markdown-only change; CI runs the whole suite), commit and push each finished step, the PR raised with the first push (one PR unless several are needed, then stacked), a draft labelled `WIP` until done, merge conflicts solved before it is finalized, then ready to merge without waiting for CI and the `in progress` label removed. Use when asked to implement, fix, work on or pick up an issue (`/dev 80`).
---

# Implement an issue

The argument is an issue number.

## 0. Gate: is it triaged, and is it free?

`gh issue view N --json labels,state`.
- If the `triaged` label is missing (or the issue is closed): **stop at once**, tell the user that the issue has not been triaged yet, and offer `/triage N`. Do nothing else.
- If the issue carries `in progress`, someone is implementing it, or was and stopped. **Do not start the implementation** unless the user asks for it specifically (e.g. "take over #80", "continue #80", "restart #80"); a plain `/dev N` is not such a request. Tell the user it is in progress, with what you find of the ongoing work (a branch or draft PR that names the issue: `gh pr list --search "N" --state open`, `git branch -a --list "*<kebab>*"`, the issue's latest comments), and stop.
- When the user does ask, there are two ways in, and the user's words decide between them:
  - **Pick up the ongoing development:** check out its branch (fresh from the remote), read its PR, commits and the comments on the issue, and carry on from there. Section 2's branch creation and 0b's claim are skipped (the label is already on); the clean working tree is still required.
  - **Restart from scratch:** a new branch off the fresh base (section 2), leaving the old branch and PR alone; close the old PR only when the user says so. Say on the issue that the work restarted, and why.
- **If it is not clear which of the two the user wants** (e.g. "take over #80" while a branch with commits exists), **ask the user** before touching anything. Never assume.

## 0b. Claim it

**As soon as the gate passes, before reading or coding:** `gh issue edit N --add-label "in progress"`, so no other agent picks it up meanwhile. The label stays on while the development lasts and **comes off when it is done** (section 6: the PR is ready). If you abandon the work, remove it too and say so on the issue.

## Throughout: the advisor, and the user's questions

### The advisor before every decision

During the development, **consult the `advisor` tool before every decision**, and then regularly: before choosing how to read the plan or where it no longer fits (section 1), before the base and the branch (section 2), before each design or implementation choice and each change of approach (section 3), when a test fails in a way you did not expect or you are stuck, and before declaring the work done (section 6). Make the work durable first (files written, a commit when one is due), then ask. Weigh its advice; when it contradicts what you found in the code or the tests, say so in a further call instead of switching silently. If the session has no `advisor` tool, or it fails, tell the user once and go on with your own judgement (questions for the user still go to the user).

### Questions go to the user

When a question comes up that the handoff, the code, CLAUDE.md and the rules do not answer, **ask the user** instead of choosing yourself. Examples: the plan does not fit the code any more, two readings of an acceptance criterion, a behaviour, wording or default the triage did not settle, something that grows the scope, a trade-off the user would notice. The advisor is asked first and its view goes into the question, but it does not take the decision. A decision the handoff records as the user's is not reopened.

- Ask as the `triage` skill does (its section *Throughout: the advisor, the user's decisions, UI and process questions*, `.claude/skills/triage/SKILL.md`): `AskUserQuestion` standing on its own, with the context (what you found, file:line, the handoff's words, test output), every option with what it leads to, your recommendation first, and a path of the user's own allowed (*Other*).
- **Mockups and diagrams are welcome**, built the same way (an Artifact, the file in the scratchpad, never in the repository): a **clickable mockup** of the options for a UI question, and **diagrams** (flow, sequence, state, timeline, whatever explains it), with a clickable walk-through when it helps, for a question about a process. As in triage, a UI question is not done until the user says so explicitly; ask the next questions it opens until then.
- Before asking, make the work durable (commit and push what is done, section 5), so nothing is lost while the question waits. Go on with the parts the answer does not touch; wait with the ones it does.
- Record each answer in the PR description and in a comment on the issue (the question, the options, the user's choice), like a deviation from the plan.

## 1. Read everything

- The issue body and **every comment**: the triage handoff is the specification (findings, code map, plan, acceptance criteria, tests, reproduction).
- Every linked issue, PR and external page (WebFetch), and the files the handoff names.
- Check the code map against current `main`: triage may predate later changes. Note what moved.
- If the plan no longer fits, say so before coding and adjust it; record the deviation in the PR and in a comment on the issue.

## 2. A branch off the latest main, in the main checkout

Every fix, feature or other piece of work gets a **new branch in the main checkout** (the repository folder itself). **No git worktree** (`git worktree add`, `EnterWorktree`) unless the user asks for one: one agent works at a time, and the LSP indexes only the main checkout.

1. **The working tree must be clean:** `git status -sb`. Uncommitted changes (the previous task's or the user's) are never stashed, committed or thrown away on your own: tell the user and ask what to do.
2. **The base, fresh from the remote.** By default the latest `main`: `git fetch origin main`. **When the user names another branch** (a stacked PR built on an open one, a release branch), that branch is the base instead: `git fetch origin <base>`, and the PR targets it. Never choose another base on your own.
3. **Create the branch from that base and switch to it:**
   `git switch -c fix/<kebab> origin/<base>` (`feature/<kebab>` for a feature; CLAUDE.md or memory may hold stricter naming; follow them). Run `mise run install` (CLAUDE.md) when the lockfiles changed since the last install.
4. **Never work on `main`** itself.

## 3. Implement

- Follow the plan and the repository's conventions (CLAUDE.md and the area's file in `.claude/rules/`: layering, i18n, accessibility, docs that must be updated with a behaviour change).
- Add the tests the plan lists; the acceptance criteria are the definition of done. A new or changed user flow always gets its Playwright test in `src/frontend/tankstat-react/e2e` (CLAUDE.md, `.claude/rules/e2e.md`), also when the plan forgot it; run that test file (`mise run test:e2e -- <file>`, `-g` for one test) before the commit that adds or changes it.
- Respect the machine's limits named in CLAUDE.md or memory (e.g. Vitest `--maxWorkers=4`, one `dotnet test` process at a time).

## 4. Tests: only the affected ones; CI runs the whole suite

- **Run only the tests the change affects**, while working and before a commit or push alike: the test files or classes you changed, those covering the code you touched (find them through the LSP's references, or the area's folder), plus what a change of its kind needs: typecheck and lint for TypeScript, a build for C#, `mise run codegen:check` after a schema or document change, `MigrationsTests` after a migration, the i18n key test after a change to `en.json` or `hu.json`, the changed Playwright file for an e2e change.
- **Not the full suite: CI runs the PR's full check**, every layer (`.github/workflows/ci.yml`). Do not run it yourself, not after a change, before a commit or push, nor before marking the PR ready.
- **No tests at all when no code changed**: a change of Markdown only (`docs/`, `README.md`, `CHANGELOG.md`, `CLAUDE.md`, `.claude/rules/`, the skills) runs no tests, no typecheck and no lint; commit and push it. The one exception: `docs/photo-reading.md`'s system prompt, contracts and JSON schema are pinned by `OpenAiCompatiblePromptTests`, so a change there runs that test.
- Red means fix first; never commit or push a test you saw fail. Report the results faithfully, and say in the PR what was run and that the rest is left to CI.

## 5. Commit and push

- **Commit and push whenever you can**: each logical step that is done and whose affected tests are green gets its commit, pushed to the branch at once, so the work is never only on this machine. Open the draft PR (section 6) with the first push.
- Commit messages in the repository's style and with its trailers (CLAUDE.md / memory). Review what is staged; never `git add -A` blindly.
- When a push is not possible (rejected, no access, offline): never force, never rebase on your own; tell the user and leave the commits on the local branch.

## 6. The pull request

- **Raise it with the first push**, not at the end: as soon as the branch's first commit is pushed, open the PR, so the work can be seen while it goes on.
- **Not finished yet** (more commits to come): the PR is a **draft** with the `WIP` label (`gh pr create --draft --label WIP`, or `gh pr ready --undo` + `gh pr edit --add-label WIP` on an existing one). Keep pushing commits to it, each with its affected tests green, and keep its description current.

### One PR, unless several are needed

- **By default the issue gets one PR.** Split the work into several only when it is needed: several features or clearly separate parts are built, to be divided between agents, or the handoff (or the user) plans increments that are each reviewed and merged on their own. A long change alone is not a reason; ask the user (advisor first) when unsure.
- **Several PRs are stacked**, so it is clear that each builds on the one before: the first branch starts from the base (section 2), each next one from the previous PR's branch, and **its PR targets that branch** (`gh pr create --base <previous branch>`). Name them as a series (`feature/<series>/01-<topic>`, `02-...`), title them `... (N/total)` and keep every title's total right when the plan grows (retitle the earlier ones, and say why). Each description names the PRs below and above it; only the last says `Closes #N`, the others `Part of #N`.
- Each PR follows the rules above on its own: raised with its first push, a draft with `WIP` while it is worked on, ready once its part is done. Do not stop at a PR's boundary to report; go on to the next one. The `in progress` claim comes off when the last one is ready.
- A change to a lower PR (a review fix) is carried up into the ones above it by merging, never by a rebase or a force push. When a lower PR is merged and GitHub has not retargeted the next one, point it at the base: `gh pr edit M --base main`.
- **Before finalizing: no merge conflicts.** Fetch the PR's base (`git fetch origin <base>`; for a stacked PR the branch below it) and check whether the branch still merges cleanly: `git merge-tree --write-tree --name-only HEAD origin/<base>` lists the conflicting files, and `gh pr view N --json mergeable,mergeStateStatus` should say `MERGEABLE` (`UNKNOWN` right after a push: ask again). If there are conflicts, **solve them before marking the PR ready**: merge the base into the branch (`git merge origin/<base>`, never a rebase or a force push), resolve each file keeping both sides' intent (read what the base's commits changed, `git log HEAD..origin/<base>`; a decision between them is a question for the user, see *Throughout*), run the affected tests of the code files that conflicted (none when only Markdown did), commit the merge and push it. A stack is checked from the bottom up, each merge carried up into the PRs above.
- **Finished** (plan done, acceptance criteria met, tests added, docs updated, the affected tests green, no merge conflicts): `gh pr edit N --remove-label WIP`, `gh pr ready N`, and bring the description up to date. **Do not wait for CI**; mark it ready at once. The development is done, so **remove the claim:** `gh issue edit N --remove-label "in progress"`, and comment on the issue with the PR link. A review fix later on (section 8) needs no new claim.
- **CI afterwards:** when CI's result arrives while the session lasts (`gh pr checks N`) and a check is red, fix it on the branch like any other step. It does not hold the PR back from being marked ready.
- The description: what changed and why, how it works, the tests run (with figures) and that the full suite is left to CI, the user's answers to questions, what is left out and why, `Closes #N`. Follow CLAUDE.md on attribution lines (e.g. no session link in PR descriptions).

## 7. Report

Summarise in chat what was done, what was verified and how, what deviates from the plan, and the PR link, with its state (draft + WIP, or ready, with the `in progress` label removed from the issue). Name the branch that holds the work.

## 8. Afterwards

- **The checkout stays on the finished branch.** The user may want to look, and the branch may still get review fixes.
- **The next issue or task starts from section 2:** a clean working tree, then a new branch off the fresh base. Only the branch is left behind; there is nothing else to clean up.
