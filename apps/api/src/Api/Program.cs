using Api.Data;
using Api.Features.Posts;
using Api.Http;
using Api.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
