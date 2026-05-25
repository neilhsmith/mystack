using System.Threading.RateLimiting;
using Api.Data;
using Api.Features.Posts;
using Api.Http;
using Api.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing connection string 'DefaultConnection'.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuditInterceptor>();

builder.Services.AddDbContext<AppDbContext>((sp, opts) =>
{
    opts.UseNpgsql(connectionString);
    opts.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddNpgSql(connectionString, name: "postgres", tags: ["ready"]);

// FluentValidation — auto-discover every AbstractValidator<T> in this assembly.
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// Mapperly-generated mappers are stateless — register one instance per feature folder.
builder.Services.AddSingleton<PostMapper>();

// RFC 7807 / 9457 problem+json for ALL error responses — wired in so unhandled
// exceptions (via UseExceptionHandler below) and bare 4xx/5xx status results (via
// UseStatusCodePages below) come back in the same shape as our 400 / 412 / 428 responses.
// The customizer attaches `traceId` to every problem response so a client report can be
// matched against server logs without the user copying anything else.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
    };
});

// Per-IP fixed-window rate limiter applied globally. Defaults intentionally strict
// (appsettings.json: 100/min) so the boilerplate is safe out of the box; dev/test get
// a lenient override via appsettings.Development.json. Health endpoints exempt — k8s/LB
// probes shouldn't be throttled. Rejected requests get RFC 7807 problem+json (same
// customizer as everything else) plus the Retry-After header per RFC 9110 §10.2.3.
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

    // Named policy used by the dev-only /v1/diagnostics/rate-limit-probe endpoint, so the
    // integration test can deterministically trip the limiter without the test factory
    // having to override global config. Applies on TOP of the global limiter (whichever
    // is stricter trips first), so an integration test fires 4 requests and gets 429.
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

// OpenAPI: native ASP.NET 10 generator. The schema transformer reflects FluentValidation
// rules into the spec so the published contract advertises the same constraints the
// runtime enforces (see FluentValidationSchemaTransformer for the mapping).
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer<FluentValidationSchemaTransformer>();
    // Emits ETag response headers and If-Match / If-None-Match request parameters
    // for endpoints marked with .WithConditionalRead() / .WithConditionalWrite() /
    // .WithEtagResponseHeader() — see ConditionalRequestOpenApi.cs.
    options.AddOperationTransformer<ConditionalRequestOperationTransformer>();
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

// Error handling — registered before any endpoint mapping so it wraps every handler.
// UseExceptionHandler: catches unhandled exceptions, hands them to ProblemDetailsService,
// emits application/problem+json with the configured customizer (traceId etc.). No stack
// trace ever leaks — same shape in Development and Production.
app.UseExceptionHandler();
// UseStatusCodePages: catches 4xx / 5xx responses that have no body (e.g. a bare
// Results.StatusCode(503)) and wraps them in problem+json too. Won't disturb handlers
// that already wrote a body (validation 400s, our 412 / 428 ProblemHttpResults, …).
app.UseStatusCodePages();
// Rate limiter runs after the error-shaping middleware so 429 rejections flow through
// the same ProblemDetails pipeline as everything else (traceId, problem+json shape).
app.UseRateLimiter();

// /openapi/v1.json — machine-readable spec. Available in all environments so client
// codegen and integration tests can rely on it; gate per-environment if that ever changes.
app.MapOpenApi();

// Versioned API surface. New resources hang off this group; bumping the API contract
// in a breaking way means creating a sibling `v2` group, not mutating `v1`.
// Health endpoints intentionally stay off the version prefix — k8s/load balancer probes
// shouldn't care about API contract version. Same for /openapi/v1.json, where the `v1`
// is the OpenAPI document version, not the API version (they happen to align today).
var v1 = app.MapGroup("/v1");

v1.MapGet("/hello", () => new { message = "hello from mystack" });

v1.MapPostsEndpoints();

// Dev-only diagnostic probes so integration tests (and humans curling locally) can
// verify cross-cutting infrastructure end-to-end. Never registered in Production.
if (app.Environment.IsDevelopment())
{
    v1.MapGet("/diagnostics/throw", static IResult () =>
        throw new InvalidOperationException("Deliberate exception for problem-details probe."));

    // 3 successful requests, then 429 on the 4th — used by RateLimitingTests to verify
    // the OnRejected handler's response shape (problem+json + traceId + Retry-After).
    v1.MapGet("/diagnostics/rate-limit-probe", () => Results.Ok(new { ok = true }))
        .RequireRateLimiting("diagnostic-strict");
}

// Aggregate — runs every registered check.
app.MapHealthChecks("/health");

// Liveness — cheap check that the process is alive. Restart container if this fails.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});

// Readiness — can the app serve traffic? Pull from load balancer if this fails.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.Run();

public partial class Program { }
