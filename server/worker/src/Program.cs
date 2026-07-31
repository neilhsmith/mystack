using System.Reflection;
using MyStack.Email;
using MyStack.Messaging;
using MyStack.Observability;

var builder = WebApplication.CreateBuilder(args);

// `Server: Kestrel` names the target for whoever is scanning and buys nothing back.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.AddObservability("worker");

builder.AddMessaging(
    "worker",
    "WorkerDb",
    options =>
    {
        // Not left to entry-assembly detection: under WebApplicationFactory the entry assembly
        // is the test host, and Wolverine would scan it instead of this app's handlers.
        options.ApplicationAssembly = typeof(Program).Assembly;

        // The pipeline's own mechanics (retry, dead-letter) stay proven by the test suite's
        // stand-in message types, discovered only under Testing — the same gate auth's
        // /debug/throw uses.
        if (builder.Environment.IsEnvironment("Testing"))
        {
            options.Discovery.IncludeAssembly(Assembly.Load("MyStack.Worker.Tests"));
        }
    }
);

builder.AddEmail();

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSecurityHeaders();
app.UseRequestLogging();

// Inside the request log, so the envelope records the 500 the client actually received.
app.UseExceptionHandler();

// Liveness deliberately checks no dependency (same reasoning as auth): a broker blip must not
// make an orchestrator restart every instance. Nothing here answers readiness yet — a real
// broker-connectivity check earns its place when an environment routes on it.
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

await app.RunAsync();

// WebApplicationFactory resolves the host through this type; top-level statements alone don't
// produce one the test project can name.
public partial class Program;
