# Validation, Mapping, and API-Contract Setup

Planning doc for a coherent strategy across DB constraints, request DTO validation, entity invariants, mapping, and the OpenAPI contract. Builds on the work already done in `apps/api/` (EF Core fluent config, ETag/conditional requests).

This doc captures the **plan** — it'll be implemented across several focused PRs in a follow-up session. Things here can still be redirected; nothing is committed code yet.

---

## 1. Why this doc exists

We already have validation duplication in the codebase:

- `AppDbContext.OnModelCreating` says `Posts.Title.IsRequired().HasMaxLength(200)`.
- `PostsEndpoints.ValidateBody` hand-checks `title.Length > 200` and "title required".

Two places, same rule, hand-synced. That'll explode as soon as we have multiple entities, more DTOs, and a TanStack Start frontend that needs to know the same constraints.

Goal: pick a strategy now, document it, then implement it incrementally so every future entity follows the same shape.

---

## 2. Strategy at a glance — Path A, code-first

We're going **code-first with shared constants**. The alternative — schema-first via TypeSpec — is more rigorously DRY but adds a toolchain layer this size of project doesn't need. Revisit only if we outgrow this.

| Layer | Source of truth | Tooling |
|---|---|---|
| DB schema (NOT NULL, max length, indexes, FKs) | EF Core fluent config in `AppDbContext.OnModelCreating` | EF Core 10 (already in place) |
| Shared scalar limits (max lengths, regex patterns, etc.) | `public const` / `public static readonly` fields on the entity | Plain C# |
| Request DTO validation (required, ranges, format, cross-field) | One validator class per DTO | **FluentValidation** |
| Entity invariants (multi-field domain rules) | Methods on the entity returning a Result-style type | `OneOf` or hand-rolled `Result<T, Error>` — defer until first real rule appears |
| DTO ↔ entity mapping | Generated mapper classes | **Mapperly** (source-generator, zero-runtime-cost) |
| API contract / OpenAPI spec | Endpoints + DTOs + FluentValidation rules | `Microsoft.AspNetCore.OpenApi` (built into ASP.NET 10) |
| TS client | _Deferred._ Not in scope for this work. | Future: `openapi-typescript` or `hey-api/openapi-ts` |

### The DRY trick

A single `public const int MaxTitleLength = 200;` on the entity is referenced by:
- The EF config (`HasMaxLength(Post.MaxTitleLength)`)
- The FluentValidation rule (`RuleFor(x => x.Title).MaximumLength(Post.MaxTitleLength)`)
- Eventually the OpenAPI spec (FluentValidation emits constraint metadata)

One change, three layers updated. No codegen, no IDL, no magic.

---

## 3. Library choices — what and why

### FluentValidation (DTO validation)

The de-facto standard for ASP.NET DTO validation. Picked over DataAnnotations because:
- Validators are separate classes — trivially unit-testable, no entity coupling.
- Far better for cross-field rules, conditional rules, custom validators.
- First-class async support (for "is this email already taken" style rules that need DB access).
- Active maintenance, broad ecosystem.

**OpenAPI integration is mandatory.** FluentValidation rules must propagate into the `/openapi/v1.json` spec so the future TS client sees them — otherwise we re-introduce the same DRY problem at the API/client boundary. Options to evaluate during implementation:

- [`FluentValidation.AspNetCore`](https://docs.fluentvalidation.net/en/latest/aspnet.html) for the validation-pipeline integration.
- [`MicroElements.OpenApi.FluentValidation`](https://github.com/micro-elements/MicroElements.OpenApi.FluentValidation) (or its successor for the new `Microsoft.AspNetCore.OpenApi` stack) to emit FluentValidation constraints into OpenAPI schema.

Verify constraint propagation end-to-end during PR 4 (see Section 5) — write a test that asserts the OpenAPI document includes the right `minLength`, `maxLength`, `pattern`, `required`, etc. for at least one DTO.

### Mapperly (DTO ↔ entity mapping)

Source-generator-based mapper. Picked over the alternatives because:
- **Source-generated**: mapping code is real C# you can read and step through, not runtime reflection magic.
- **Zero runtime cost**: it's just method calls.
- **Compile-time safe**: missing/extra/mistyped properties fail the build.
- AutoMapper has fallen out of favour for exactly these reasons (runtime cost, indirection, hidden behaviour).

Convention: one `partial` mapper class per feature folder, e.g. `Features/Posts/PostMappings.cs`:

```csharp
[Mapper]
public partial class PostMappings
{
    public partial PostResponse ToResponse(Post post);
    public partial Post ToEntity(CreatePostRequest request);
    public partial void Apply(UpdatePostRequest source, Post target);
}
```

Replace the existing hand-written `PostMappings.ToResponse(this Post)` extension with a Mapperly-generated equivalent during PR 3.

### `OneOf` or hand-rolled Result (entity invariants) — deferred

Don't add a Result library until the first real domain rule appears. CRUD-only Posts doesn't need it. When it does land, lean toward [`OneOf`](https://github.com/mcintyre321/OneOf) (small, well-known, fits .NET idioms) over building our own — unless we want the discipline of explicit error types per feature.

---

## 4. Conventions for future entities

Once this is in place, the "Adding a new entity" flow in the `backend-dev` skill grows two more bullets:

1. Define `public const`s for shared scalar limits on the entity (max lengths, regex patterns).
2. Reference those constants from `AppDbContext.OnModelCreating`.
3. Add a Mapperly mapper class in the feature folder.
4. Add FluentValidation validator classes for each request DTO, referencing the entity constants.
5. Wire validators into the endpoint pipeline (via filter — see PR 2 below).

The skill update is part of the implementation work, not separate. Document the convention with a worked example pointing at the Posts feature.

---

## 5. Implementation plan — PR breakdown

Each PR is self-contained, leaves the system shippable, and can be paused if direction changes.

### PR 1 — Extract validation constants to the Post entity

**Goal:** kill the existing duplication; establish the shared-constant pattern.

- Add `public const int MaxTitleLength = 200;` (and any other shared limits we identify) to `Post`.
- `AppDbContext.OnModelCreating` uses `Post.MaxTitleLength` instead of the literal.
- `PostsEndpoints.ValidateBody` references `Post.MaxTitleLength`.
- Update tests to reference the constant where they assert "title too long" behaviour.
- Document the pattern in the `backend-dev` skill ("Conventions for new entities" subsection).

No new dependencies. Smallest possible step that proves the pattern.

### PR 2 — Add FluentValidation + endpoint filter

**Goal:** replace the hand-rolled `ValidateBody` with FluentValidation, integrate with the minimal-API pipeline.

- Add `FluentValidation` (and likely `FluentValidation.DependencyInjectionExtensions`) packages.
- `Features/Posts/CreatePostRequestValidator.cs` and `Features/Posts/UpdatePostRequestValidator.cs`. Reference `Post.MaxTitleLength`.
- Endpoint filter — `ValidationEndpointFilter<T>` — that runs the validator and short-circuits with `Results.ValidationProblem(...)` (RFC 7807 problem+json) on failure. Apply via `.AddEndpointFilter<...>()` or a `.WithValidation<T>()` group convention.
- Delete `PostsEndpoints.ValidateBody`.
- Validators must be discoverable via DI (`services.AddValidatorsFromAssemblyContaining<Program>()`).
- Update tests: validation tests still hit the endpoints; add unit tests for the validators in `Api.Tests.Unit/`.

Decision point during implementation: do validators short-circuit *before* or *after* the existence/precondition checks? Current `PostsEndpoints` order is `validate → fetch (404) → If-Match (428/412) → apply`. Validation should remain first — a malformed body shouldn't burn a DB lookup or precondition check. The filter approach makes this automatic.

### PR 3 — Adopt Mapperly

**Goal:** establish Mapperly as the project's mapping norm.

- Add the `Riok.Mapperly` package.
- Replace the hand-written `PostMappings` extensions with a Mapperly `[Mapper] partial class PostMappings`.
- Update call sites in `PostsEndpoints`.
- Add a worked-example section to the `backend-dev` skill: "Mapping convention for feature folders."
- Verify tests still pass (mapping shouldn't change behaviour, just generate the mapper).

### PR 4 — OpenAPI exposure + FluentValidation rule propagation

**Goal:** publish `/openapi/v1.json` that reflects FluentValidation constraints, so the future TS client (out of scope here) can rely on it.

- Add `Microsoft.AspNetCore.OpenApi` (built into ASP.NET 10).
- Map `/openapi/v1.json` (and optionally `/scalar` or `/swagger` for human reading — pick one during implementation).
- Integrate FluentValidation rule propagation into the OpenAPI schema. Confirm at least `maxLength`, `minLength`, `required`, and `pattern` flow through for the Posts DTOs.
- Add an integration test that asserts the spec contains the expected constraints for a representative DTO. This is the regression guard for "did somebody add a validator rule but forget to confirm it shows up in the spec?"
- Document the OpenAPI endpoint in `CLAUDE.md` (Current state) and in the `backend-dev` skill (so agents know to check the spec when changing validators).

### PR 5 (deferred, not in this batch) — Result-style entity invariants

When the first multi-field domain rule appears, add `OneOf` (or roll our own `Result<T, Error>`) and refactor that one feature to use it. Skip until a real need exists.

---

## 6. Things to revisit / open questions

- **Validator location convention.** Validators per-DTO in the feature folder, or a `Validators/` subfolder per feature? Lean toward flat in the feature folder (matches Mapperly mappers, keeps related code together) but worth confirming once PR 2 is drafted.
- **OpenAPI doc URL.** Are we exposing `/openapi/v1.json` only, or also a human UI (Scalar, Swagger UI, Redoc)? Scalar feels most modern; pick during PR 4.
- **Async validators.** When we add "is this username taken?" style rules that hit the DB, decide whether validators get scoped DI for `AppDbContext`. FluentValidation supports this; just call it out so we don't accidentally inject singletons.
- **ProblemDetails shape.** Both validation errors (400/422) and ETag precondition failures (412/428) should use the same `application/problem+json` shape. They already do for ETags. Confirm validation does too (FluentValidation's built-in integration emits `ValidationProblemDetails` — should compose cleanly).
- **Test layout.** Validator unit tests go in `Api.Tests.Unit/`, one class per validator. Validation behaviour-at-the-endpoint stays in the existing `PostsEndpointsTests`. Keep the "one file per API resource" rule from the `backend-dev` skill — validator unit tests are a *different boundary* (validator → result), so a separate file is right.

---

## 7. Out of scope for this work

- TypeScript client codegen. Will be picked up when `apps/web` becomes a real project.
- Entity invariants library (`OneOf` / `Result`). Wait for a real domain rule.
- TypeSpec / schema-first. Reconsider only if the code-first DRY pattern breaks down.
- AutoMapper, MediatR, or other heavier frameworks. Explicitly avoided.

---

## 8. Implementation checklist (to be ticked off across PRs)

- [ ] PR 1 — entity constants + EF config + ValidateBody references the constants
- [ ] PR 2 — FluentValidation packages, validators, endpoint filter, tests
- [ ] PR 3 — Mapperly package, Posts mapper, skill update
- [ ] PR 4 — `Microsoft.AspNetCore.OpenApi`, FluentValidation→OpenAPI integration, spec test
- [ ] `backend-dev` skill updated with the new "Conventions for new entities" bullets, the mapping convention, and the OpenAPI/spec-test guidance
- [ ] `CLAUDE.md` Current-state bullets added for FluentValidation, Mapperly, OpenAPI
