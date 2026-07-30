using MyStack.Auth.Data;
using MyStack.Auth.Health;
using MyStack.Auth.Oidc;
using MyStack.Auth.Security;
using MyStack.Auth.Telemetry;
using MyStack.Observability;

var builder = WebApplication.CreateBuilder(args);

// `Server: Kestrel` names the target for whoever is scanning and buys nothing back.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.AddObservability("auth");

builder.Services.AddProblemDetails();
builder.Services.AddAuthSecurityHeaders();
builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddAuthIdentity();
builder.AddAuthOpenIddict();
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
