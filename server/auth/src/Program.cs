using MyStack.Auth.Data;
using MyStack.Auth.Health;
using MyStack.Auth.Security;
using MyStack.Observability;

var builder = WebApplication.CreateBuilder(args);

// `Server: Kestrel` names the target for whoever is scanning and buys nothing back.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.AddObservability("auth");

builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddAuthIdentity();
builder.Services.AddAuthHealthChecks();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthSecurityHeaders();

app.UseAuthentication();
app.UseActorEnrichment();
app.UseAuthorization();

app.MapAuthHealthChecks();

await app.RunAsync();

// WebApplicationFactory resolves the host through this type; top-level statements alone don't
// produce one the test project can name.
public partial class Program;
