using MyStack.Auth.Account;
using MyStack.Auth.Data;
using MyStack.Auth.ErrorHandling;
using MyStack.Auth.Health;
using MyStack.Auth.Messaging;
using MyStack.Auth.Oidc;
using MyStack.Auth.Security;
using MyStack.Auth.Telemetry;
using MyStack.Email;
using MyStack.Messaging;
using MyStack.Observability;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

// `Server: Kestrel` names the target for whoever is scanning and buys nothing back.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.AddObservability("auth");

builder.Services.AddProblemDetails();
builder.Services.AddAuthSecurityHeaders();
builder.AddAuthRateLimiter();
builder.AddAuthDatabase();
builder.AddAuthIdentity();
builder.AddAuthOpenIddict();
builder.AddMessaging(
    "auth",
    DatabaseExtensions.ConnectionStringName,
    options =>
    {
        // Not left to entry-assembly detection: under WebApplicationFactory the entry assembly
        // is the test host, and Wolverine would scan it instead of this app's handlers.
        options.ApplicationAssembly = typeof(Program).Assembly;

        // The EF outbox: account pages publish through IDbContextOutbox<AuthDbContext>, so a
        // user write and its outgoing email commit in one transaction (architecture §3.3).
        options.UseEntityFrameworkCoreTransactions();

        // auth consumes its own maintenance messages; account emails are cross-app work,
        // published to the worker's queue and delivered there.
        options.PublishMessage<PruneOidcTokens>().ToRabbitQueue("auth");
        options.PublishMessage<SendEmail>().ToRabbitQueue("worker");
    }
);
builder.Services.AddScheduledMessage<PruneOidcTokens>("0 3 * * *");
builder.AddAccountFlows();
builder.Services.AddSingleton<AuthMetrics>();
builder.Services.AddRazorPages();
builder.Services.AddAuthHealthChecks();
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthSecurityHeaders();
app.UseRequestLogging();

// Inside the request log, so the envelope records what the client actually received: the
// status-code shaping and the exception handler, ordered as the extension explains.
app.UseAuthErrorHandling();

app.UseStaticFiles();

// Explicit rather than implicit routing: the error-page re-execution has to re-match the
// request, so the matcher must sit inside the status-code shaping.
app.UseRouting();
app.UseRateLimiter();

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
