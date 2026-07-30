using MyStack.Auth.Data;
using MyStack.Auth.Health;
using MyStack.Auth.Messaging;
using MyStack.Auth.Oidc;
using MyStack.Auth.Security;
using MyStack.Auth.Telemetry;
using MyStack.Messaging;
using MyStack.Observability;
using Wolverine;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

// `Server: Kestrel` names the target for whoever is scanning and buys nothing back.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.AddObservability("auth");

builder.Services.AddProblemDetails();
builder.Services.AddAuthSecurityHeaders();
builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddAuthIdentity();
builder.AddAuthOpenIddict();
builder.AddMessaging(
    "auth",
    DatabaseExtensions.ConnectionStringName,
    options =>
    {
        // Not left to entry-assembly detection: under WebApplicationFactory the entry assembly
        // is the test host, and Wolverine would scan it instead of this app's handlers.
        options.ApplicationAssembly = typeof(Program).Assembly;

        // auth consumes its own maintenance messages; cross-app work (email) goes to the
        // worker's queue when it exists.
        options.PublishMessage<PruneOidcTokens>().ToRabbitQueue("auth");
    }
);
builder.Services.AddHostedService<PruneScheduler>();
builder.Services.AddSingleton<AuthMetrics>();
builder.Services.AddRazorPages();
builder.Services.AddAuthHealthChecks();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthSecurityHeaders();
app.UseRequestLogging();

// Inside the request log, so the envelope records the 500 the client actually received.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseActorEnrichment();
app.UseAuthorization();

app.MapAuthHealthChecks();
app.MapAuthOidcEndpoints();
app.MapRazorPages().WithSecurityHeadersPolicy(SecurityHeaderExtensions.PagesPolicy);

if (app.Environment.IsEnvironment("Testing"))
{
    // The exception handler's ProblemDetails contract is only provable by throwing beneath it.
    app.MapGet(
        "/debug/throw",
        string () => throw new InvalidOperationException("Deliberate test failure.")
    );
}

await app.RunAsync();

// WebApplicationFactory resolves the host through this type; top-level statements alone don't
// produce one the test project can name.
public partial class Program;
