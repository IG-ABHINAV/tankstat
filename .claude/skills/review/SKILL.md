---
name: review
description: Review a GitHub pull request - only one that carries neither `reviewing` nor `reviewed`, claim it with the `reviewing` label, review it with `code-review` (Claude Code's built-in skill, or the same review by hand in other agents; its effort levels, `--fix` and `--max-findings` passed on) and report the findings to the user (posted on the PR only with `--comment`, inline, or `--summarize`, one summary comment), then approve it when no required change is left (with `--comment` and one open: changes requested; on the `gh` account's own PR a label instead), label it `reviewed` and release the claim. Reads the related issues (and checks one the user names against the PR), consults the advisor on every question and asks the user, with the context, when it gives no clear answer. Use when asked to review a PR (`/review 99`, `/review next max --fix`, "review the next PR"). Changes the PR's code only with `--fix`, on the PR's branch in the main checkout.
---

# Review a pull request

## Tools in any agent

This skill works in any coding agent that reads `SKILL.md` files (Claude Code from `.claude/skills/`, others from `.agents/skills/`, the same files). Where it names a capability, use your agent's equivalent:

- **The advisor:** a second opinion from a stronger reviewer that sees the whole session (Claude Code: the `advisor` tool; elsewhere a reviewer model or subagent your agent offers, given the task, what you found and the question). Without one, say so once and go on with your own judgement; the user still takes every decision this skill gives them.
- **Asking the user:** a question that stands on its own, with the options and your recommendation first, answered by picking one or writing their own (Claude Code: `AskUserQuestion`; elsewhere a message with numbered options, then wait for the answer).
- **The code review:** Claude Code's built-in `code-review` skill, with its effort levels, `--max-findings`, `--fix` and `--comment`. Elsewhere, review the diff yourself at the depth the level asks for (from a few high-confidence findings at `low` to every finding, some uncertain, at `max`), apply fixes only with `--fix`, and post inline comments only with `--comment`. `ultra` exists only in Claude Code and only the user can launch it.
- **Code lookups:** the LSP where your agent has one (Claude Code: the `LSP` tool), grep otherwise.
- **No second checkout:** no `git worktree` and no isolated copy of the repository (Claude Code: `EnterWorktree`) unless the user asks for one.
- **The project's rules** apply to every agent: CLAUDE.md from *Toolchain and commands* on, the `.claude/rules/` file of each area, and the attribution lines CLAUDE.md names (use your own tool's line in place of "Generated with Claude Code"). Where this skill says "memory", that is Claude Code's memory; other agents skip it.

## Arguments

`/review <target> [level] [options]`, in any order after the target:

| Argument | Meaning |
| --- | --- |
| `N` | The PR number. |
| `next` | The oldest open, non-draft PR that has neither the `reviewing` nor the `reviewed` label and was not opened by Dependabot (`gh pr list --search "draft:false -label:reviewing -label:reviewed -author:app/dependabot sort:created-asc"`); say which one you picked and why. |
| `low` `medium` `high` `xhigh` `max` | The effort level, passed on to `code-review`. Without one, `code-review` reuses the level typed last. |
| `--max-findings <n>` / `all` / `default` | Passed on to `code-review` as given (it remembers the choice until `default`). |
| `--fix` | After the review, apply the findings to the PR's branch and push them (section 3b). Without it the PR's code is never changed. |
| `--comment` | Passed on to `code-review`, which then posts each finding as an inline PR comment. |
| `--summarize` | After the review, post one summary comment on the PR (section 3). |
| `#M` / `issue M` (one or more) | An issue the user names with the PR, for its context: checked against the PR in step 2 and read like the related issues. |

Not available: `ultra` (and with it `--post` / `--no-post`). It is billed and only the user can launch it; if asked for, say so and offer `/code-review ultra N` for them to type. Any other argument: ask what is meant instead of guessing.

By default **nothing is posted on the PR**: the findings are reported to the user in this session (step 5), and the PR only gets its labels, its verdict (step 4: an approval when nothing required is left) and with `--fix` the commits. `--comment` and `--summarize` (either or both) put the findings on the PR, for an author who does not see this conversation.

## Throughout: the advisor on every question

**Whenever a question comes up, consult the advisor first**, before asking the user or deciding on your own. That covers every point where this skill says to ask (another agent's claim, a named draft, `--fix` on a fork or a Dependabot PR, a working tree that is not clean, a branch that moved or cannot be fast-forwarded, a rejected push, an issue the user named that does not fit the PR, an argument you do not know). It also covers the questions of the review itself: whether a finding is real, its severity, whether it goes against what a related issue agreed, and under `--fix` whether to fix a finding or leave it. Make the work durable first (a commit when one is due), then ask it.

- **A clear answer:** follow it, and say in the report what was asked and what the advisor answered. When it contradicts what you found in the code, the tests or an issue, say so in a further call instead of switching silently.
- **No clear answer** (it weighs both ways, says the choice is the user's, or still conflicts with your evidence after that further call): **ask the user, with the context**, since they may not see the steps before the question. Say what the question is and what you found (file:line, issue and PR numbers, commits, test output), give the options and what each leads to, and include the advisor's view.
- **The rules are not questions:** the advisor cannot clear what this skill forbids doing on your own (stashing, committing or throwing away uncommitted changes, a force push or a rebase after a rejected push, changing code without `--fix`, a commit on a Dependabot branch). There its answer only shapes the question, and the user decides.
- If your agent has no advisor, or it fails, tell the user once, then ask them where the skill says to ask and use your own judgement for the rest.

## 0. Gate: is it open and free?

`gh pr view N --json state,isDraft,labels,headRefOid,headRefName,isCrossRepository,author`.
- Closed or merged: **stop**, tell the user, do nothing else.
- `reviewing`: another agent is reviewing it right now. **Do not start.** Tell the user and **ask what to do**: take it over, or leave it. Only when the user has already said they know and want it taken over (e.g. "take over the review of #99") is there nothing to ask; say you are taking it over.
- `reviewed`: it has been reviewed already. **Do not review it again** unless the user asks for a new review explicitly (e.g. "review #99 again", after new commits); otherwise tell the user and stop. On a new review, remove `reviewed` when you claim it (step 1).
- A draft (`isDraft`, usually labelled `WIP`) is not finished: `next` never picks one; a named draft is reviewed only after telling the user it is still a draft.
- `--fix` on a PR from a fork (`isCrossRepository`): its branch is not ours to push to. Tell the user and ask whether to review it without `--fix`.
- A Dependabot PR (`author.login` is `app/dependabot`): `next` never picks one; a named one is reviewed only after telling the user, and never with `--fix`: a commit of ours on its branch stops Dependabot from rebasing it. A finding there is fixed on a branch of its own.

## 1. Claim it

**As soon as the gate passes, before reading the PR:** `gh pr edit N --add-label reviewing` (with `--remove-label reviewed` on a new review), so no other agent picks it up meanwhile. If the label is missing on the repository, create it first: `gh label create reviewing --description "Being reviewed right now by an agent; others skip it unless asked" --color D4C5F9 --force`.

The label comes off again at the end (step 4b), after `reviewed` went on. If you abandon the review, remove it too and tell the user; with `--comment` or `--summarize`, say so on the PR as well.

## 2. Read the context

- `gh pr view N --comments`: the description, every comment and review (`--json body,comments,reviews` when the plain view prints nothing outside a terminal).
- **Always read the related issues**, not only the one the PR closes: they say what changed and why, and what was decided along the way. Collect them from:
  - the issues the PR closes (`gh pr view N --json closingIssuesReferences`);
  - every `#M` in the PR's title, description, comments and reviews, its commit messages (`gh pr view N --json commits`) and its branch name;
  - the issues and PRs that mention the PR (`gh api repos/{owner}/{repo}/issues/N/timeline --paginate --jq '.[] | select(.event == "cross-referenced") | .source.issue.number'`);
  - one step further: the issues those name in their own text or comments (the earlier bug a fix builds on, the parent of a series), without following their links in turn.

  Read each one with its comments (`gh issue view M --comments`; a number that is a PR, `gh pr view M --comments`). The closing issue's triage handoff is the specification: its plan and acceptance criteria say what the PR is meant to do. The others are background: an earlier fix the change must not undo, a decision the PR follows, a known follow-up it leaves on purpose. A finding that contradicts what an issue agreed is reported with that issue's number; one an issue already settled is not a finding.
- **An issue the user named with the PR** (`#M`, `issue M`, or in their words: "review 255, it is for #243"): say whether it is among the related issues found above (seen, and how: closed by the PR, named in its commits, mentioning it, one step away) or was not. Then check the relation yourself: read it and compare what it asks with what the diff changes (the files, the behaviour, the area). If it is not related (another area, another bug, or a number that is a PR, closed long before, or does not exist), **warn the user before the review starts**: what the issue is about, what the PR changes, why they do not match, and ask whether they meant another issue or another PR, or want the review to go on without it. If it is related but the PR does not link it, say so, and say it once more in the report; the PR's links are not yours to change unless the user asks.
- Note the head commit (`headRefOid`); the review is of that commit.
- Check CLAUDE.md and the `.claude/rules/` files of the areas the diff touches.

## 3. Review

Run the code review on the PR (see *Tools in any agent*): `code-review` with the argument `N`, plus the level, `--max-findings`, `--fix` and `--comment` exactly as the user gave them (`--comment` only when the user typed it). It does the review itself; follow its instructions. With `--fix`, check out the PR's branch as section 3b says **first** and invoke it there, so the fixes land in the PR's branch and nowhere else.

With `--summarize`, then add one summary comment on the PR (`gh pr comment N`): the head commit reviewed, the level, how many findings and of what kind, anything checked by hand (and how), whether you see something blocking, without `--comment` every finding itself (file:line, what goes wrong and when, its severity), and with `--fix` which findings were fixed (the commits) and which were left and why. When there are no findings, say so. Without `--summarize`, post no comment of your own.

Without `--fix`, **never change the PR's code**: no commits, no pushes. Applying the findings is then a separate task (the author's, or `/dev`).

## 3b. `--fix`: the PR's branch in the main checkout

**No second checkout** (see *Tools in any agent*) unless the user asks for one: one agent works at a time, and the LSP indexes only the main checkout.

1. **The working tree must be clean:** `git status -sb`. Uncommitted changes are never stashed, committed or thrown away on your own: tell the user and ask what to do.
2. `git fetch origin <headRefName>` and check that `origin/<headRefName>` is the `headRefOid` noted in step 2; if it moved, the review is of the new head: say so and note it again.
3. **Switch the main checkout to the PR's branch:** `git switch <headRefName>` (it tracks `origin/<headRefName>`), then `git merge --ff-only origin/<headRefName>` to bring a local copy of it up to date. If the local branch has commits the remote lacks, or cannot be fast-forwarded, stop and ask the user. Run `mise run install` (CLAUDE.md) when the lockfiles changed since the last install.
4. After `code-review` applied its fixes: run **only the affected tests** (the changed test files, those covering the code touched, plus typecheck and lint for TypeScript, a build for C#; none when only Markdown changed), within the machine's limits. **Not the full suite: CI runs the PR's full check** on the push. Red means fix or take the change back; never commit failing tests.
5. Commit in logical commits (CLAUDE.md / memory: split, trailers), staged by hand, never `git add -A`.
6. **Push to the PR's branch without force:** `git push origin HEAD:<headRefName>`. A rejected push means someone pushed meanwhile: stop, do not force, do not rebase on your own; tell the user and leave the commits on the local branch.
7. The checkout stays on the PR's branch; the next task starts from a clean working tree, like `/dev`'s.

## 4. The verdict: approve, or changes required

When the review is done (and with `--fix` the fixes pushed), decide the verdict.

- **A required change** is a finding that must be fixed before the PR is merged: a confirmed bug (wrong behaviour, lost or leaked data, a crash, a security hole), an acceptance criterion of the issue not met, a test that fails, a test the rules demand that is missing (a new flow without its Playwright test), a hard rule of CLAUDE.md or `.claude/rules/` broken (i18n, accessibility, the layers, a migration for every provider). Not required: simplifications, naming and style, an uncertain finding (not confirmed), a problem that was there before the PR, a follow-up. A finding `--fix` fixed and pushed is no longer open. When you cannot tell whether a finding is required, it is a question (*Throughout*: the advisor, then the user).
- **No required change open: approve the PR.**
- **A required change open:** with `--comment`, **mark the PR as changes required**; without `--comment`, **do not approve it**, and post nothing: the verdict is in the report (step 5) only.

How, depending on who opened the PR. GitHub does not let an account approve or request changes on its own PR, so compare the author (`author.login`, step 0) with the account `gh` uses (`gh api user --jq .login`):

| Verdict | PR opened by someone else (a contributor, Dependabot) | PR opened by the `gh` account itself |
| --- | --- | --- |
| Approved | `gh pr review N --approve --body "<one line: what was reviewed, at which commit>"` | the label `approved` only, no review posted |
| Changes required, with `--comment` | `gh pr review N --request-changes --body "<the required findings, one line each, pointing to their inline comments>"` | the label `changes requested` only |
| Changes required, without `--comment` | nothing | nothing |

- A missing label is created first: `gh label create approved --description "Reviewed by an agent: nothing required left" --color 0E8A16 --force`, `gh label create "changes requested" --description "Reviewed by an agent: required changes open" --color B60205 --force`.
- The two labels exclude each other: setting one removes the other, and a verdict of "nothing" removes both (a new review replaces an old verdict). An earlier approval of ours on someone else's PR that a new review no longer supports is replaced by the request for changes (with `--comment`) or dismissed (`gh api -X PUT repos/{owner}/{repo}/pulls/N/reviews/<id>/dismissals -f message="..." -f event=DISMISS`, without it).

## 4b. Bookkeeping

`gh pr edit N --add-label reviewed --remove-label reviewing`: the review is done (and with `--fix` the fixes pushed) and the claim is released. The PR's other labels (`WIP` and the like) are not yours to change, apart from `approved` / `changes requested` (step 4).

## 5. Report

In chat: the PR reviewed (link, head commit, level), the related issues read (numbers, one line each on what they added; an issue the user named: whether it was found, how it relates, and that the PR does not link it when it does not), every finding (file:line, what goes wrong and when, its severity; or none), anything checked by hand, whether something blocks, the verdict (approved, or changes required with the required findings named) and how it was given (an approval or a request for changes, the `approved` / `changes requested` label on a PR of the `gh` account's own, or, changes required without `--comment`, nothing on the PR), what was posted on the PR (inline comments, the summary comment's link, or nothing), and the labels it now carries. Without `--fix`: no code was changed; say so. With `--fix`: what was fixed and pushed (commits), what was left, the affected tests run and their results (the full check left to CI), and the branch the checkout is on.

## Rules

- Respect the machine's limits named in CLAUDE.md or memory (e.g. Vitest `--maxWorkers=4`, one `dotnet test` process at a time) for anything you run to check a finding.
- Facts over guesses: a finding without a file:line reference and a concrete failure scenario is marked as uncertain.
- New commits pushed after the review do not take `reviewed` off; a new review only on request (step 0).
