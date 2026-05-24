# mystack

Personal full-stack boilerplate. Work in progress.

## Status

**v0** — bare skeleton with agent workflow scaffolding. See [docs/features/bootstrap/initial.md](docs/features/bootstrap/initial.md) for what exists and what's planned.

## Quick start

```bash
# Run the API
dotnet run --project apps/api/src/Api

# Should respond at http://localhost:5xxx/hello
```

## Project structure

```
apps/      Application code (api, web)
packages/  Shared TypeScript packages
infra/     Docker, compose files
tests/     Cross-cutting tests (E2E)
scripts/   Dev scripts
docs/      Documentation
.claude/   Claude Code skills + hooks + settings
.github/   GitHub Actions + PR template
```

## Working with agents

This repo is set up to be developed primarily via Claude Code agents creating pull requests for human review. See [CLAUDE.md](CLAUDE.md) for the workflow.
