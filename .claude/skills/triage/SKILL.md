---
name: triage
description: Triage a GitHub issue - claim it with the `triaging` label, understand it, reproduce it, find the cause, write a fix plan another agent can pick up, post it on the issue, label it `triaged` and release the claim. Consults the advisor regularly, asks the user at every decision point, and asks a UI question with a clickable mockup of the options (a path of the user's own allowed), going on with questions until the user says it is done; a process question with diagrams of it (and a clickable walk-through when it helps). Skips issues another agent is triaging unless the user names one. Use when asked to triage, analyse or investigate an issue (`/triage 80`, "triage the next untriaged issue"). Never implements the fix.
---

# Triage an issue

The argument is an issue number, or `next` (pick the oldest open issue that has neither the `triaged` nor the `triaging` label; say which one you picked and why). Everything you learn goes **on the issue**, so that anyone, human or agent, can start the fix without re-deriving it. The `dev` skill refuses an issue that is not `triaged`.

## Tools in any agent

This skill works in any coding agent that reads `SKILL.md` files (Claude Code from `.claude/skills/`, others from `.agents/skills/`, the same files). Where it names a capability, use your agent's equivalent:

- **The advisor:** a second opinion from a stronger reviewer that sees the whole session (Claude Code: the `advisor` tool; elsewhere a reviewer model or subagent your agent offers, given the task, what you found and the question). Without one, say so once and go on with your own judgement; the user still takes every decision this skill gives them.
- **Asking the user:** a question that stands on its own, with the options and your recommendation first, answered by picking one or writing their own (Claude Code: `AskUserQuestion`; elsewhere a message with numbered options, then wait for the answer).
- **A clickable mockup or diagram page:** one self-contained HTML page the user opens and clicks through, never in the repository (Claude Code: an Artifact, after loading `artifact-design`, and `artifact-diagramming` for diagrams; elsewhere an HTML file in a scratch folder outside the repository, its path given to the user). Republishing it keeps its link or path.
- **Code lookups:** the LSP where your agent has one (Claude Code: the `LSP` tool), grep otherwise.
- **The project's rules** apply to every agent: CLAUDE.md from *Toolchain and commands* on, the `.claude/rules/` file of each area, and the attribution lines CLAUDE.md names (use your own tool's line in place of "Generated with Claude Code"). Where this skill says "memory", that is Claude Code's memory; other agents skip it.

## Claiming the issue

- `triaging` means another agent is on it right now. **Skip such issues by default**; take one only when the user names it explicitly (`/triage 80` on an issue labelled `triaging` is such a request; `next` never is).
- **As soon as you have picked an issue**, before any analysis: `gh issue edit N --add-label triaging`, so no other agent picks it up meanwhile.
- The label comes off again at the end (step 7), after `triaged` went on. If you abandon the triage, remove it too and say so on the issue.

## Throughout: the advisor, the user's decisions, UI and process questions

### The advisor, regularly

**Consult the advisor regularly**, not only once: after restating the problem (step 1), before choosing how to reproduce it (step 3), when a reproduction does something you did not expect or you are stuck, before settling on the cause (step 4), before the plan (step 5), and before posting the handoff (step 6). Weigh its advice; when it contradicts what you found in the code or a reproduction, say so in a further call instead of switching silently. If your agent has no advisor, or it fails, tell the user once and go on with your own judgement (decision points still go to the user).

### Decision points go to the user

A **decision point** is any place where the fix could go more than one reasonable way and the code, CLAUDE.md, the rules and the issue do not settle it. Examples: what the app should do (a behaviour, a wording, a default), which reading of a vague issue is meant, what is in scope and what becomes an issue of its own, a trade-off between approaches the user would notice (a migration, a setting, a breaking change). **Ask the user; do not decide it yourself.** The advisor is asked first, and its view goes into the question, but it never takes the decision.

- Ask the user, the question standing on its own, since the user may see only the dialog: the question, the context (what you found, file:line, the issue's words, the reproduction), every option with what it leads to (behaviour, size of the fix, risks), and your recommendation first, marked *(Recommended)*. *Other* is always there for a path of their own; say so when the options are not exhaustive.
- Purely technical choices that the code's conventions already decide (where a helper lives, which test layer) are not decision points; follow the conventions and say so in the plan.
- The handoff records each decision: the question, the options, **what the user chose** (in their words when they added any), and that it was the user's choice. `dev` follows it and never reopens it.

### UI questions: a clickable mockup, and not done until the user says so

A decision point about the UI (a layout, a control, a dialog, a flow, a wording on screen, a state such as empty, loading, error or offline) is always asked **with a clickable mockup of the options**:

- **Build it as a clickable mockup page** (see *Tools in any agent*). It is a single page, its file outside the repository: a triage changes no source file. In it, each option is a mockup you can click through, switched by tabs or a toggle and named exactly as in the question (*A*, *B*, ...). The buttons, menus, dialogs and steps react to clicks, so the flow can be tried, not just looked at.
- **Make it look like the app:** the theme's colours in the light and the dark scheme, the real English strings (from `en.json`, or the new ones proposed, marked as new), and a phone and a desktop width (a switch, or side by side). Cover the states that matter to the question, and the touch-target and accessibility rules of `frontend.md`. A short note under each option says what it changes and what it costs.
- **Then ask** the user: the mockup's link or path, the options by their names in it, your recommendation first. Also tell them they can take **a path of their own**: describe it in *Other*, or ask for a change to an option ("B, but the button on the left").
- **Not done until the user says so explicitly.** A choice is not the end. Fold it into the mockup (republish the same file: same link or path), then **ask the next questions**: the details the choice opens (each state, the wording, the phone layout, keyboard and screen reader, edge cases), and every round **ask whether the UI is settled or there is more to change**. Keep going, round after round, until the user says in so many words that it is done ("done", "that's it", "settled"). Silence, a choice on its own, or "looks good" followed by a further remark is not done.
- Until then the triage is **not finished**: the issue keeps `triaging`, gets no `triaged`, and no handoff is posted on it. If the user goes away, the work waits; tell them where it stands and what the open question is.
- The handoff describes the settled UI precisely enough to build without the mockup: the layout at both sizes, every string (English and the Hungarian to add), the states, the interactions, and the accessibility. It gives the mockup's link for reference (say when it is private to the user).

### Process questions: diagrams, and a clickable walk-through when it helps

When a question is about a **process** (how something moves through the app or the server: a user flow across pages, sign-in and sessions, offline sync and its outbox, a backup or restore, the setup, a notification's way from producer to bell, a scheduled job, an import, or the order of steps in one of them), **prepare diagrams before asking**, so the user can understand the problem in depth and not only pick an answer. Publish them as a diagram page (see *Tools in any agent*; outside the repository, as for a mockup), together with the clickable mockup when there is one:

- **Choose the diagrams that explain it:** a flow chart for the steps and branches, a sequence diagram for who calls whom (browser, service worker, API, database, an outside provider) and in what order, a state diagram for an entity's or a connection's states, a timeline for what happens when (timers, retries, a restart), a data-flow picture for where data is kept and copied. Use as many as the question needs, and none that only repeats another.
- **Show the current behaviour first**, from the code (file:line in a note beside each step) and the reproduction, with **where it goes wrong marked**. Then show each option as its own diagram, named as in the question (*A*, *B*, ...), with what it changes made visible: the steps added, removed or moved. Show the cases that decide between them too (offline, two devices, a failure halfway, a restart).
- **A clickable walk-through when the process has choices or steps a person takes:** a mockup that steps through the process (next and back, a choice at each branch, the path taken highlighted on the diagram), so each option can be tried from start to end. The same look and rules as a UI mockup.
- **Then ask** as for any decision point (standing on its own): the page's link or path, the options by their names in it, your recommendation first, and the user's own path allowed. Fold each answer into the diagrams (same file, same link) and ask what it leaves open.
- The handoff carries the settled process as a Mermaid diagram (GitHub draws a ` ```mermaid ` block in a comment), with the steps described in words beside it and the page's link for reference.

## Steps

1. **Read everything.** `gh issue view N --comments`; follow every linked issue, PR and external page (WebFetch); read the files the issue names. Restate the problem in one paragraph before touching code.
2. **Find the code.** Map where the behaviour lives (file:line). Check CLAUDE.md and the area's file in `.claude/rules/` for its rules and conventions.
3. **Reproduce it.** Prefer real evidence over reasoning: a throwaway unit test or probe, a real-browser run (Playwright), a request against a running server. Say plainly whether it is **confirmed**, **partly reproduced** or **not reproduced**, and what you ran. Throwaway probes live in a scratch folder outside the repository or are deleted before you finish; **never commit them and never change source files** while triaging.
4. **Find the cause.** Explain the mechanism with file:line references and the evidence. If the issue's description turns out wrong or incomplete, say so and correct the title.
5. **Plan the fix.** First settle every decision point with the user, and every UI question until the user says it is done (see above); the plan builds on their answers. Then numbered steps, each with an acceptance criterion; the tests to add (unit / integration / real browser; a new or changed user flow always names its Playwright test in `src/frontend/tankstat-react/e2e`); docs to update; what is out of scope; related issues.
6. **Write the handoff comment** on the issue, in this order: the problem in one paragraph; verified facts (a table if there are several cases); code map; the decisions (each question, its options, the user's choice; the settled UI with the mockup's link; the settled process as a Mermaid diagram); plan with acceptance criteria; tests; how to reproduce it (commands and scripts verbatim, in a `<details>` block when long); pitfalls you hit; related issues. State the commit of `main` it was checked against.
7. **Bookkeeping.**
   - `gh issue edit N --add-label triaged --remove-label triaging`: the handoff is posted, the issue is ready for `dev`, and the claim is released.
   - A duplicate: keep the one with the better discussion, close the other with `gh issue close M --duplicate-of N` plus the `duplicate` label, and carry anything the kept one lacks into its handoff.
   - A separate bug found on the way: file it (`gh issue create --label bug`) with what you know; do not fix it.
8. **Report** in chat: a short summary of the cause, the decisions the user took, the plan and the link to the comment. No code was changed; say so.

## Rules

- Only the tests you need for reproduction; never the full suite.
- Respect the machine's limits named in CLAUDE.md or memory (e.g. Vitest `--maxWorkers=4`, one `dotnet test` process at a time).
- Facts over guesses: a claim without a reproduction or a file:line reference is marked as such.
