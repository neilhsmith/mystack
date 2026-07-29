using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MyStack.Observability;

/// <summary>
/// Starts the trace and meter providers in the host's Starting phase. OpenTelemetry's own hosted
/// service starts them in the later Start phase — after every <c>StartingAsync</c> has run — which
/// would leave boot-phase work like the database migrator invisible to tracing. Registered by
/// <see cref="ObservabilityExtensions.AddObservability{TBuilder}"/>, which therefore has to be
/// called before any hosted service whose boot work should be traced.
/// </summary>
internal sealed class TelemetryBootstrap(IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        // Resolving the providers is what builds them and registers their listeners.
        services.GetService<TracerProvider>();
        services.GetService<MeterProvider>();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
