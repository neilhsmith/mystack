using System.Threading.RateLimiting;
using Api.Authorization;
using Api.Data;
using Api.Features.Auth;
using Api.Features.Auth.Seeding;
using Api.Features.Posts;
using Api.Http;
using Api.Identity;
using Api.Rbac;
using Api.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenIddict.Validation.AspNetCore;
using OpenIddictClaims = OpenIddict.Abstractions.OpenIddictConstants.Claims;
using OpenIddictScopes = OpenIddict.Abstractions.OpenIddictConstants.Scopes;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing connection string 'DefaultConnection'.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuditInterceptor>();

// AppDbContext now owns three things at once: business entities (Posts), ASP.NET Identity
// (users + roles), and OpenIddict's server stores (applications, authorizations, scopes,
// tokens). Single DbContext, single migrations history, single Postgres database. The
// `.UseOpenIddict()` call registers OpenIddict's EF Core mappings against THIS context —
// mirror the same call in DesignTimeDbContextFactory so migrations stay in sync.
builder.Services.AddDbContext<AppDbContext>((sp, opts) =>
{
    opts.UseNpgsql(connectionString);
    opts.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
    opts.UseOpenIddict();
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

// FluentValidation — auto-discover every AbstractValidator<T> in this assembly.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Mapperly-generated mappers are stateless — register one instance per feature folder.
builder.Services.AddSingleton<PostMapper>();

// Per-feature service classes own DbContext interaction and return ErrorOr<T>; scoped so
// they share the request's AppDbContext. Endpoints stay thin — see PostsEndpoints.
builder.Services.AddScoped<PostsService>();

// ---------------- Identity + Auth ----------------

// ASP.NET Identity wired against AppDbContext. Cookie scheme is the application's
// Identity.Application — used by the /Account/Login Razor Page and the /connect/authorize
// challenge. Token storage is provided by OpenIddict (below), not Identity's own.
builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        // Sane production-ish defaults. Tighten per-environment via appsettings when you
        // know your threat model.
        options.User.RequireUniqueEmail = true;

        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);

        // OpenIddict expects subject claim type "sub", not the legacy long URI.
        options.ClaimsIdentity.UserIdClaimType = OpenIddictClaims.Subject;
        options.ClaimsIdentity.UserNameClaimType = OpenIddictClaims.Name;
        options.ClaimsIdentity.RoleClaimType = OpenIddictClaims.Role;
        options.ClaimsIdentity.EmailClaimType = OpenIddictClaims.Email;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.LogoutPath = "/connect/endsession";
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

// ---------------- OpenIddict ----------------

// One AddOpenIddict() call configures three things:
//   - Core: EF Core stores against AppDbContext (managed apps/auths/scopes/tokens).
//   - Server: the OAuth/OIDC protocol implementation (issues tokens).
//   - Validation: validates bearer tokens for resource-server endpoints (in-process,
//     no JWKS roundtrip since the issuer lives here).
builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.UseEntityFrameworkCore()
            .UseDbContext<AppDbContext>();
    })
    .AddServer(options =>
    {
        // OAuth + OIDC endpoints. Match the routes mapped in AuthEndpoints.
        options
            .SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token")
            .SetUserInfoEndpointUris("/connect/userinfo")
            .SetEndSessionEndpointUris("/connect/endsession");

        // Enabled grant types — modern best practice: Auth Code + PKCE for interactive
        // clients, Client Credentials for service-to-service, Refresh Token for long
        // sessions. NO implicit, NO password grant.
        options
            .AllowAuthorizationCodeFlow()
            .RequireProofKeyForCodeExchange()
            .AllowClientCredentialsFlow()
            .AllowRefreshTokenFlow();

        // Token lifetimes — read from config so deployers can tune per environment;
        // fallback to spec-reasonable defaults if unset.
        var accessMinutes = builder.Configuration.GetValue<int?>("Auth:AccessTokenLifetimeMinutes") ?? 15;
        var refreshDays = builder.Configuration.GetValue<int?>("Auth:RefreshTokenLifetimeDays") ?? 14;
        var authCodeMinutes = builder.Configuration.GetValue<int?>("Auth:AuthorizationCodeLifetimeMinutes") ?? 5;
        var identityMinutes = builder.Configuration.GetValue<int?>("Auth:IdentityTokenLifetimeMinutes") ?? 15;

        options
            .SetAccessTokenLifetime(TimeSpan.FromMinutes(accessMinutes))
            .SetRefreshTokenLifetime(TimeSpan.FromDays(refreshDays))
            .SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(authCodeMinutes))
            .SetIdentityTokenLifetime(TimeSpan.FromMinutes(identityMinutes));

        // Refresh-token rotation: every exchange issues a new refresh token and revokes
        // the previous. Pair with reasonable lifetimes above. (OpenIddict 7.x rotates by
        // default when sliding=true is configured via SetRefreshTokenReuseLeeway = 0.)
        options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

        // Register the scopes the server will issue. Custom scopes are registered as data
        // by OpenIddictSeeder; here we declare them so the issued tokens carry the right
        // resource claim and clients can request them.
        options.RegisterScopes(
            OpenIddictScopes.Email,
            OpenIddictScopes.Profile,
            OpenIddictScopes.Roles,
            OpenIddictScopes.OfflineAccess,
            Scopes.Read,
            Scopes.Write);

        // Signing & encryption — development uses ephemeral keys (regenerated per
        // process). Production should load real X.509 certs via configuration. The
        // ephemeral keys are fine for the dev cycle because tokens are short-lived and
        // a restart invalidating in-flight tokens is acceptable.
        if (builder.Environment.IsDevelopment())
        {
            options.AddDevelopmentEncryptionCertificate();
            options.AddDevelopmentSigningCertificate();
        }
        else
        {
            // Production: load from "Auth:Certificates:Signing" / "Auth:Certificates:Encryption"
            // configuration — fail fast if absent, don't silently fall back to ephemeral.
            throw new NotImplementedException(
                "Production signing/encryption certs are not yet wired. " +
                "Configure Auth:Certificates:* and load them here before deploying.");
        }

        // Disable JWT encryption for access tokens so the API can read them directly
        // without the encryption key. Tokens remain signed; payload-confidentiality is
        // provided by TLS in transit (this matches what the vast majority of resource
        // servers expect). Re-enable encryption if access tokens need to traverse
        // untrusted intermediaries.
        options.DisableAccessTokenEncryption();

        // ASP.NET Core integration — pass-through means OpenIddict parses and validates
        // protocol parameters, then hands the request off to the routed endpoint (see
        // AuthEndpoints).
        var serverAspNetCore = options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough();

        // OpenIddict rejects non-HTTPS traffic by default (spec-required for production).
        // Local dev runs on http://localhost; relax the requirement only in Development.
        if (builder.Environment.IsDevelopment())
        {
            serverAspNetCore.DisableTransportSecurityRequirement();
        }
    })
    .AddValidation(options =>
    {
        // Validate bearer tokens locally (same process issues them) — no HTTP round-trip
        // to a JWKS endpoint. The ASP.NET Core integration plugs the validation handler
        // into the authentication pipeline as a named scheme.
        options.UseLocalServer();
        options.UseAspNetCore();

        // The audience the API accepts. Custom scopes register this value as a resource
        // (see OpenIddictSeeder.SeedScopesAsync), so issued access tokens carry it as
        // their `aud` claim. The validation pipeline then matches before letting the
        // request through.
        options.AddAudiences(AuthAudiences.ApiAudience);
    });

// Default authentication scheme: cookie (Identity.Application) — used by the Razor Pages
// login flow and the /connect/authorize challenge. Bearer-token resource-server endpoints
// declare their scheme through the authorization policies built by
// DynamicAuthorizationPolicyProvider.

// ---------------- Authorization (scopes + permissions) ----------------

builder.Services.AddAuthorization(options =>
{
    // Fallback policy: every endpoint requires authentication unless it explicitly opts
    // out via .AllowAnonymous(). Belt-and-braces for new endpoints — forget to add
    // .RequirePermission(...) and the request still 401s rather than serving anon.
    //
    // Schemes listed here MUST be registered (otherwise the policy evaluator throws
    // "No authentication handler is registered for the scheme '<name>'" on every request
    // before AllowAnonymous can short-circuit). The bearer scheme handles API tokens; the
    // Identity application cookie handles browser-based flows.
    options.FallbackPolicy = new AuthorizationPolicyBuilder(
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            IdentityConstants.ApplicationScheme)
        .RequireAuthenticatedUser()
        .Build();
});

// Replace the default policy provider with our dynamic one so endpoints can use
// .RequireAuthorization("scope:mystack.read") / .RequireAuthorization("perm:posts.read")
// without each scope/permission being declared up front. Keep this single instance so
// authorization options bound elsewhere still work.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, DynamicAuthorizationPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

// ---------------- RBAC catalog ----------------

builder.Services.AddSingleton<RolePermissionCatalog>();
builder.Services.AddSingleton<RolePermissionCatalogRefresher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RolePermissionCatalogRefresher>());

// ---------------- Auth seeders & options ----------------

builder.Services.Configure<AuthSeedOptions>(builder.Configuration.GetSection(AuthSeedOptions.SectionName));
builder.Services.AddScoped<AuthClaimsBuilder>();
builder.Services.AddScoped<RbacSeeder>();
builder.Services.AddScoped<UserSeeder>();
builder.Services.AddScoped<OpenIddictSeeder>();
// AuthSeedHostedService runs migrations (dev) + all seeders + primes the catalog.
// Replaces the inline "if Dev, MigrateAsync" block from the previous Program.cs so the
// auth-related startup work has a single, ordered owner.
builder.Services.AddHostedService<AuthSeedHostedService>();

// ---------------- ProblemDetails + diagnostics ----------------

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    };
});

builder.Services.AddExceptionHandler<DbUpdateConcurrencyExceptionHandler>();

// ---------------- Rate limiting (unchanged) ----------------

builder.Services.AddRateLimiter(options =>
{
    var permitLimit = builder.Configuration.GetValue<int?>("RateLimiting:PermitLimit") ?? 100;
    var windowSeconds = builder.Configuration.GetValue<int?>("RateLimiting:WindowSeconds") ?? 60;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            return RateLimitPartition.GetNoLimiter("health-exempt");
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    options.AddFixedWindowLimiter("diagnostic-strict", o =>
    {
        o.PermitLimit = 3;
        o.Window = TimeSpan.FromSeconds(60);
        o.QueueLimit = 0;
        o.AutoReplenishment = true;
    });

    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        }

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too Many Requests",
                detail: "Rate limit exceeded. Try again later.")
            .ExecuteAsync(context.HttpContext);
    };
});

// ---------------- OpenAPI ----------------

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<FluentValidationSchemaTransformer>();
});

// ---------------- Razor Pages (auth UI) ----------------

builder.Services.AddRazorPages();

var app = builder.Build();

// ---------------- HTTP pipeline ----------------

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

app.UseMiddleware<EtagMiddleware>();

// Authentication runs before authorization. Both must precede the endpoint dispatch.
// Cookie + bearer schemes share the same middleware — schemes are selected per-policy.
app.UseAuthentication();
app.UseAuthorization();

// OpenAPI spec is the API contract — clients (codegen, dashboards, the test suite)
// need to read it without holding a token. Same reasoning applies to the OIDC discovery
// + JWKS endpoints, which OpenIddict registers internally and doesn't pass through the
// authorization middleware.
app.MapOpenApi().AllowAnonymous();

// Razor Pages — currently just the auth UI under /Account/* and the landing /. The
// individual pages opt into / out of auth via [AllowAnonymous] on the page model.
app.MapRazorPages();

// OAuth / OIDC protocol endpoints (/connect/authorize, /connect/token, /connect/userinfo,
// /connect/endsession). Anonymous by design where appropriate.
app.MapAuthEndpoints();

// Versioned API surface. New resources hang off this group; bumping the API contract in
// a breaking way means creating a sibling `v2` group, not mutating `v1`. The fallback
// authorization policy means every endpoint added here requires auth unless it explicitly
// .AllowAnonymous()s out.
var v1 = app.MapGroup("/v1");

v1.MapGet("/hello", () => new { message = "hello from mystack" })
    .AllowAnonymous();

v1.MapPostsEndpoints();

// Dev-only diagnostic probes so integration tests can verify cross-cutting infrastructure
// end-to-end. Never registered in Production.
if (app.Environment.IsDevelopment())
{
    v1.MapGet("/diagnostics/throw", static IResult () =>
            throw new InvalidOperationException("Deliberate exception for problem-details probe."))
        .AllowAnonymous();

    v1.MapGet("/diagnostics/throw-concurrency", static IResult () =>
            throw new DbUpdateConcurrencyException("Deliberate exception for 409 mapping probe."))
        .AllowAnonymous();

    v1.MapGet("/diagnostics/rate-limit-probe", () => Results.Ok(new { ok = true }))
        .RequireRateLimiting("diagnostic-strict")
        .AllowAnonymous();
}

// Health endpoints — unversioned and anonymous (k8s / load-balancer probes).
app.MapHealthChecks("/health").AllowAnonymous();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

app.Run();

public partial class Program { }
