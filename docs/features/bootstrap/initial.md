# mystack — Bootstrap (v0)

This is the seed doc for **mystack**, a personal full-stack boilerplate. The goal of this v0 is **not** to build everything at once — it's to get the repository, folder skeleton, agent workflow, and CI/CD scaffolding in place so we can iterate on the rest via the normal PR-review loop with agents going forward.

There's a much larger architecture conversation that produced this doc — that conversation will be referenced in the future to flesh out features (auth, multi-tenancy, observability, RBAC, events, etc.). For now: minimal viable boilerplate + working agent → PR loop.

---

## 1. What mystack is

A monorepo template for spinning up new personal projects with minimal setup friction. Eventually it'll include:

- ASP.NET API + TanStack Start BFF
- Postgres + EF Core
- OIDC auth (OpenIddict)
- Docker-based dev environment
- Comprehensive testing (unit / integration / E2E)
- Observability, email, events, RBAC, etc.

But **none of that is in v0.** v0 is a bare skeleton + the workflow to add the rest safely.

The architecture is well-documented in a separate planning conversation; agents working on this repo should follow CLAUDE.md and the skill files as those get added in subsequent PRs.

---

## 2. v0 scope (what we're actually doing in this first session)

This is the only checklist for the v0 PR:

1. Initialize the git repository
2. Create the planned folder skeleton (with `.gitkeep` files where empty)
3. Add a minimal ASP.NET 10 API in `apps/api/` with a single `GET /hello` endpoint that returns `{"message": "hello from mystack"}`
4. Add a `CLAUDE.md` seed file (minimal — gets fleshed out later)
5. Add a `README.md` with quick-start
6. Add a `.gitignore` covering .NET, Node, and IDE artifacts
7. Add a `.editorconfig` with consistent formatting rules
8. Set up GitHub Actions for PR validation
9. Configure the repo for the agent-led PR workflow (branch protection guidance, PR template, commit conventions)
10. First end-to-end test of the workflow: agent makes a change, opens a PR, human reviews

That's it. No frontend yet. No database yet. No auth yet. No tests yet beyond what ASP.NET ships with by default. Those come in subsequent PRs.

---

## 3. Folder skeleton

Create this structure even if most folders are empty (use `.gitkeep` to commit empty dirs):

```
mystack/
├── .github/
│   ├── workflows/
│   │   └── pr.yml                    # CI for PR validation
│   ├── pull_request_template.md
│   └── CODEOWNERS                    # optional, owner = you
├── .claude/
│   ├── skills/                       # to be filled in over time
│   │   └── .gitkeep
│   ├── hooks/                        # to be filled in over time
│   │   └── .gitkeep
│   └── settings.json                 # MCP config (filesystem, postgres later)
├── apps/
│   ├── api/                          # ASP.NET API (v0: just /hello)
│   └── web/                          # TanStack Start app (v0: empty + .gitkeep)
│       └── .gitkeep
├── packages/                         # Shared TS packages (v0: empty)
│   └── .gitkeep
├── infra/                            # Docker, compose files (v0: empty)
│   └── .gitkeep
├── tests/                            # Cross-cutting tests (v0: empty)
│   └── e2e/
│       └── .gitkeep
├── scripts/                          # Dev scripts (v0: empty)
│   └── .gitkeep
├── docs/                             # Documentation (v0: this doc)
│   └── bootstrap.md                  # copy of this file
├── CLAUDE.md                         # Agent context
├── README.md
├── .editorconfig
├── .gitignore
└── .gitattributes
```

---

## 4. The ASP.NET API skeleton (apps/api/)

Goal: minimal runnable .NET 10 web API. Just enough to verify the repo works and CI can build it.

```
apps/api/
├── src/
│   └── Api/
│       ├── Program.cs
│       └── Api.csproj
├── Api.sln
└── .gitignore                        # .NET-specific overrides
```

Commands to set up:

```bash
cd apps/api
dotnet new sln -n Api
mkdir -p src/Api
cd src/Api
dotnet new web -n Api --framework net10.0
cd ../..
dotnet sln add src/Api/Api.csproj
```

Then replace `Program.cs` with:

```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/hello", () => new { message = "hello from mystack" });

app.Run();
```

Verify it runs:

```bash
dotnet run --project src/Api
# In another terminal:
curl http://localhost:5xxx/hello   # port shown in dotnet run output
```

---

## 5. CLAUDE.md (seed content)

Create `CLAUDE.md` at the repo root with this content:

````markdown
# mystack

A personal full-stack boilerplate. **Currently in v0** — this is a bare skeleton plus the agent/PR workflow. Most features are not yet implemented and will be added via subsequent PRs.

## Current state

- ASP.NET 10 API at `apps/api/` with a single `/hello` endpoint
- Folder skeleton in place for `apps/web`, `packages/`, `infra/`, `tests/`, `scripts/`
- GitHub Actions PR validation workflow
- This file (CLAUDE.md), README, .editorconfig, .gitignore

## Planned (not yet built)

The full architecture vision lives in `docs/bootstrap.md` and will be implemented via PRs. High-level: TanStack Start BFF + ASP.NET API, Postgres + EF Core, OpenIddict auth, Hangfire, Docker compose dev env, comprehensive testing.

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
dotnet build apps/api/Api.sln
```
````

More commands will be added as the stack grows.

## Things never to do (current list — will grow)

- Don't push directly to main
- Don't force-push to PR branches without being asked
- Don't add architecture pieces (auth, DB, etc.) unless the task explicitly asks for them
- Don't run destructive git commands (`git reset --hard origin/main`, `git push --force`, etc.) without explicit confirmation
- Don't commit secrets, API keys, or anything from `.env*` files

## Skill files

`.claude/skills/` will be populated as patterns emerge. Currently empty. When adding skills, follow the convention of one folder per skill with a `SKILL.md` inside.

````

---

## 6. README.md (seed content)

```markdown
# mystack

Personal full-stack boilerplate. Work in progress.

## Status

**v0** — bare skeleton with agent workflow scaffolding. See `docs/bootstrap.md` for what exists and what's planned.

## Quick start

```bash
# Run the API
dotnet run --project apps/api/src/Api

# Should respond at http://localhost:5xxx/hello
````

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

This repo is set up to be developed primarily via Claude Code agents creating pull requests for human review. See `CLAUDE.md` for the workflow.

````

---

## 7. .gitignore

```gitignore
# .NET
bin/
obj/
*.user
*.suo
.vs/

# Node
node_modules/
.pnpm-store/
dist/
build/
.turbo/

# Editors / OS
.vscode/
.idea/
*.swp
.DS_Store
Thumbs.db

# Env / secrets
.env
.env.local
.env.*.local
*.pem
secrets/

# Logs
*.log
npm-debug.log*

# Coverage / test output
coverage/
.coverage/
TestResults/

# Build artifacts
*.dll
*.exe
*.pdb
````

---

## 8. .editorconfig

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space
indent_size = 2

[*.{cs,csproj,sln}]
indent_size = 4

[*.md]
trim_trailing_whitespace = false

[Makefile]
indent_style = tab
```

---

## 9. .gitattributes

```
* text=auto eol=lf
*.{cmd,[cC][mM][dD]} text eol=crlf
*.{bat,[bB][aA][tT]} text eol=crlf
*.png binary
*.jpg binary
*.gif binary
*.ico binary
```

---

## 10. GitHub Actions — PR validation

Create `.github/workflows/pr.yml`:

```yaml
name: PR Validation

on:
  pull_request:
    branches: [main]

permissions:
  contents: read
  pull-requests: write

jobs:
  dotnet:
    name: .NET Build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - name: Restore dependencies
        run: dotnet restore apps/api/Api.sln

      - name: Build
        run: dotnet build apps/api/Api.sln --configuration Release --no-restore

      # Tests will be added once we have test projects
      # - name: Test
      #   run: dotnet test apps/api/Api.sln --configuration Release --no-build

  # Node lint/typecheck/test will be added in a future PR when web/ exists
```

---

## 11. PR template

Create `.github/pull_request_template.md`:

```markdown
## What

<!-- Brief description of what this PR does -->

## Why

<!-- Why is this change needed? Link to issue if applicable. -->

## How

<!-- Implementation notes — anything non-obvious about the approach -->

## Decisions made

<!--
If this PR includes decisions that weren't pre-specified, call them out here.
Examples:
- "Used X over Y because..."
- "Named the table `foo_bar` rather than `foobars` because..."
- "Skipped adding migration for the rename — used [SQL] in a one-off script instead"
-->

## Things to check

<!--
What should the reviewer pay particular attention to?
- File X has the main logic
- Test coverage focuses on Y
- Manual verification done: ...
-->

## Checklist

- [ ] Tests pass locally (`dotnet test` / `pnpm test` as applicable)
- [ ] No secrets or credentials committed
- [ ] Branch named `agent/<description>` or `<your-initials>/<description>`
- [ ] Conventional commit messages
- [ ] PR description explains the _why_, not just the _what_
```

---

## 12. .claude/settings.json (seed)

```json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "."]
    }
  }
}
```

Postgres MCP added later once DB exists.

---

## 13. Repository setup steps (do these in order)

```bash
# 1. Create the folder and initialize git
mkdir mystack && cd mystack
git init
git branch -M main

# 2. Create the folder skeleton with .gitkeep files
mkdir -p .github/workflows .claude/skills .claude/hooks \
         apps/api apps/web packages infra tests/e2e scripts docs
touch apps/web/.gitkeep packages/.gitkeep infra/.gitkeep \
      tests/e2e/.gitkeep scripts/.gitkeep \
      .claude/skills/.gitkeep .claude/hooks/.gitkeep

# 3. Create the ASP.NET API
cd apps/api
dotnet new sln -n Api
mkdir -p src/Api
cd src/Api
dotnet new web -n Api --framework net10.0 --force
# (replace Program.cs with the /hello endpoint from section 4)
cd ../..
dotnet sln add src/Api/Api.csproj
cd ../..

# 4. Create all the root files from sections 5-12:
#    - CLAUDE.md
#    - README.md
#    - .gitignore
#    - .editorconfig
#    - .gitattributes
#    - .github/workflows/pr.yml
#    - .github/pull_request_template.md
#    - .claude/settings.json
#    - docs/bootstrap.md (copy of this file)

# 5. Verify the API builds and runs
dotnet build apps/api/Api.sln
dotnet run --project apps/api/src/Api
# Test: curl http://localhost:5xxx/hello

# 6. Initial commit
git add .
git commit -m "chore: initial mystack skeleton with .NET 10 hello world API

- Folder skeleton (apps, packages, infra, tests, scripts, docs, .claude, .github)
- ASP.NET 10 API with single GET /hello endpoint
- CLAUDE.md with agent workflow rules
- GitHub Actions PR validation workflow
- PR template, .editorconfig, .gitignore, .gitattributes
- README with quick-start"

# 7. Create the GitHub repo and push
gh repo create mystack --private --source=. --remote=origin --push
# Or manually:
#   git remote add origin git@github.com:<you>/mystack.git
#   git push -u origin main
```

---

## 14. Branch protection (do this on GitHub after first push)

In **Settings → Branches → Branch protection rules → Add rule for `main`**:

- ✅ Require a pull request before merging
- ✅ Require status checks to pass before merging
  - Select: `.NET Build` (will appear after first PR run)
- ✅ Require conversation resolution before merging
- ✅ Do not allow bypassing the above settings
- (Optional) ✅ Restrict who can push to matching branches

This enforces the "agents can never push to main" rule structurally.

---

## 15. First agent-driven PR (test of the workflow)

Once the repo is initialized and pushed, the immediate next step is to verify the workflow end-to-end. Pick a tiny task — e.g., "add a `/health` endpoint to the API that returns `{ status: 'ok' }`" — and have an agent:

1. Create branch `agent/add-health-endpoint`
2. Add the endpoint
3. Commit with `feat: add /health endpoint`
4. Push the branch
5. Open a PR using `gh pr create` (or via the GitHub UI)
6. Fill in the PR template
7. Wait for your review

You review, request changes if any, merge. That cycle proves the workflow before you start using it for substantial changes.

---

## 16. After v0 is merged — the path forward

Each subsequent feature gets its own PR. Rough sequencing of next PRs:

1. **PR #2**: Add Docker Compose with Postgres + a docker-compose.yml at infra/
2. **PR #3**: Add EF Core, AppDbContext, first migration
3. **PR #4**: Add FastEndpoints, refactor /hello into a feature folder
4. **PR #5**: Add OpenIddict skeleton
5. **PR #6**: Set up the TanStack Start app at apps/web/
6. **PR #7**: Add the catch-all proxy + iron-session
7. ... (and so on through the architecture)

Each PR is small enough to review thoroughly. Each one references back to the architecture conversation when picking specific approaches.

---

## 17. Open notes for the next session

When kicking off the Claude Code session in the `mystack/` folder, tell the agent:

> Start with section 13 of `docs/bootstrap.md`. Execute the setup steps in order. Stop after step 5 (verify the API builds) and confirm before doing the initial commit. Don't push to GitHub yet — I'll set that up myself after I review the local state.

That gives you a chance to inspect before anything goes remote.

Once pushed and branch protection is configured, run the section-15 test ("add /health endpoint") as the first real agent-driven PR to verify the workflow works.

---

## Reference

Full architecture decisions live in the planning conversation that produced this doc. Reference it when subsequent PRs need to implement features like auth, multi-tenancy, RBAC, event bus, etc.
