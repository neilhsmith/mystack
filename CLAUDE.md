# mystack

A personal full-stack boilerplate. **Currently in v0** — this is a bare skeleton plus the agent/PR workflow. Most features are not yet implemented and will be added via subsequent PRs.

## Current state

- ASP.NET 10 API at `apps/api/` with a single `/hello` endpoint
- Folder skeleton in place for `apps/web`, `packages/`, `infra/`, `tests/`, `scripts/`
- GitHub Actions PR validation workflow
- This file (CLAUDE.md), README, .editorconfig, .gitignore

## Planned (not yet built)

The full architecture vision lives in [docs/features/bootstrap/initial.md](docs/features/bootstrap/initial.md) and will be implemented via PRs. High-level: TanStack Start BFF + ASP.NET API, Postgres + EF Core, OpenIddict auth, Hangfire, Docker compose dev env, comprehensive testing.

Agents working in this repo: **do not invent architecture decisions.** When something isn't documented yet, ask first. The user has strong opinions about how this should be built.

## Workflow rules (CRITICAL)

These rules govern how agents interact with this repo:

1. **Never push to main.** All changes go through pull requests.
2. **Create a feature branch** for every task, named `agent/<short-description>` (e.g., `agent/add-user-endpoint`).
3. **Commit messages** follow Conventional Commits: `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`, `ci:`.
4. **Open a PR** when work is complete (or at a logical stopping point for human review).
5. **PR description** must include:
   - What changed and why
   - Anything the human should pay attention to during review
   - Any decisions made that weren't pre-specified (call these out explicitly)
6. **Wait for human review.** Don't merge your own PRs.
7. **Respond to review feedback** by pushing additional commits to the same branch, not by force-pushing or rebasing (unless explicitly asked).
8. **When unsure, ask** rather than guess. The cost of asking is low; the cost of doing the wrong thing and creating cleanup work is high.

## Current commands

```bash
# Run the API
dotnet run --project apps/api/src/Api

# Build everything
dotnet build apps/api/Api.slnx
```

More commands will be added as the stack grows.

## Things never to do (current list — will grow)

- Don't push directly to main
- Don't force-push to PR branches without being asked
- Don't add architecture pieces (auth, DB, etc.) unless the task explicitly asks for them
- Don't run destructive git commands (`git reset --hard origin/main`, `git push --force`, etc.) without explicit confirmation
- Don't commit secrets, API keys, or anything from `.env*` files

## Skill files

`.claude/skills/` will be populated as patterns emerge. Currently empty. When adding skills, follow the convention of one folder per skill with a `SKILL.md` inside.
