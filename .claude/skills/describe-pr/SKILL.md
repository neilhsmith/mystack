---
name: describe-pr
description: Write the PR title and body that will become the squash commit. Run before every `gh pr create` to compose the message, and again on an open PR after pushing more work or immediately before merging.
---

# Describe PR

`main` is squash-merge only, and this repo is configured so the squash commit's **title is the PR
title** and its **body is the PR body**. Branch commits contribute their diff and nothing else —
their messages are discarded. So the PR body is the permanent history entry, and `git log` on `main`
is the sequence of PR bodies.

This composes that entry from the diff. Run it when opening a PR, and again whenever the branch has
moved since the message was written, so the message always describes the result — never the plan.

## Steps

1. **Find the PR** for the current branch:
   `gh pr view --json number,title,body,baseRefName,url`. No PR means you are composing the message
   for `gh pr create`: follow the same steps, then pass the result as `--title`/`--body`.

2. **Read what actually landed.** `git diff <base>...HEAD` and `git log <base>..HEAD --oneline`.
   The diff is the source of truth. Treat any existing body as an unverified draft: check each of
   its claims against the diff rather than editing around them.

3. **Write the title** as a conventional commit — `type(scope): subject`, imperative and
   lower-case. Never include the PR number: merge order won't match PR numbering, so ` (#N)`
   suffixes read as random in `git log`. GitHub pre-fills one in the merge box — remind the user to
   delete it there when reporting blockers, and pass `--subject` explicitly if merging with
   `gh pr merge`.

4. **Write the body** into these sections, dropping any that would be empty:
   - what changed, and why
   - non-obvious constraints or gotchas found while building it
   - what it deliberately doesn't do, and why

   Every statement must be verifiable in the diff. Cut anything describing the journey rather than
   the destination: options considered and rejected, review back-and-forth, "addressed feedback",
   and verification logs whose result wasn't surprising.

5. **Check the repo's conventions** against the diff, and fix or report:
   - docs updated in the same PR as the code they describe
   - `docs/architecture.md` §7's inventory ticked for anything that landed

6. **Apply it.** On an existing PR, show the before/after of the title and body, then
   `gh pr edit`. When opening, use the message with `gh pr create` directly.

7. **Report blockers** — gate conclusion, unresolved review threads, mergeability. Skip this on a
   PR you just created: the gate hasn't run yet. Never merge; that's the user's call.

## Guardrails

- Never describe something the diff doesn't contain, however true it was earlier in the PR.
- A superseded decision is noise, not history. Delete it.
- Keep the section shape identical across PRs. `git log` is only greppable if every entry looks the
  same.
- **Never hard-wrap the body.** GitHub re-wraps the squash message at 72 columns, one source line
  at a time, and does not reflow paragraphs — so a body wrapped at 100 becomes 72 characters plus a
  ~28-character remainder on every line, permanently ragged in `git log`. Write one unwrapped line
  per paragraph and per bullet. Manual continuation indents survive as stray indentation for the
  same reason: don't add them.
- The squash message comes from the PR, not from the branch commits — except `Co-authored-by:`
  trailers, which GitHub aggregates across the squashed commits and appends on its own. Any other
  footer that belongs in `main`'s history has to be in the body.
