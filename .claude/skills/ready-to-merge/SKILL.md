---
name: ready-to-merge
description: Rewrite the current PR's title and body to describe what actually landed, then report anything blocking the merge. Run immediately before merging a PR.
disable-model-invocation: true
---

# Ready to merge

`main` is squash-merge only, and this repo is configured so the squash commit's **title is the PR
title** and its **body is the PR body**. Branch commits contribute their diff and nothing else —
their messages are discarded. So the PR body is the permanent history entry, and `git log` on `main`
is the sequence of PR bodies.

A body written when the PR opened describes the plan. This rewrites it to describe the result.

## Steps

1. **Find the PR** for the current branch:
   `gh pr view --json number,title,body,baseRefName,url`. If there isn't one, stop and say so.

2. **Read what actually landed.** `git diff <base>...HEAD` and `git log <base>..HEAD --oneline`.
   The diff is the source of truth. Treat the existing body as an unverified draft: check each of
   its claims against the diff rather than editing around them.

3. **Rewrite the title** as a conventional commit — `type(scope): subject`, imperative and
   lower-case. GitHub appends ` (#N)` itself.

4. **Rewrite the body** into these sections, dropping any that would be empty:
   - what changed, and why
   - non-obvious constraints or gotchas found while building it
   - what it deliberately doesn't do, and why

   Every statement must be verifiable in the diff. Cut anything describing the journey rather than
   the destination: options considered and rejected, review back-and-forth, "addressed feedback",
   and verification logs whose result wasn't surprising.

5. **Check the repo's conventions** against the diff, and fix or report:
   - docs updated in the same PR as the code they describe
   - `docs/architecture.md` §7's inventory ticked for anything that landed

6. **Show the before/after** of the title and body, then apply with `gh pr edit`.

7. **Report blockers** — gate conclusion, unresolved review threads, mergeability. Never merge;
   that's the user's call.

## Guardrails

- Never describe something the diff doesn't contain, however true it was earlier in the PR.
- A superseded decision is noise, not history. Delete it.
- Keep the section shape identical across PRs. `git log` is only greppable if every entry looks the
  same.
- Trailers on branch commits (`Co-Authored-By`, attribution footers) do not survive the squash. If
  one belongs in `main`'s history, it has to be in the body.
