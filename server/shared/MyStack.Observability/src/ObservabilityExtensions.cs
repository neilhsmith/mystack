using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MyStack.Observability;

public static class ObservabilityExtensions
{
    public static TBuilder AddObservability<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        // The W3C trace id on every console line. OTLP carries it natively; the console gets it as
        // a scope, which is why scopes are switched on here rather than left to configuration.
        builder.Logging.Configure(logging =>
            logging.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId
        );
        builder.Logging.AddSimpleConsole(console =>
        {
            console.IncludeScopes = true;
            console.TimestampFormat = "HH:mm:ss ";
        });

        var telemetry = builder
            .Services.AddOpenTelemetry()
            .ConfigureResource(resource =>
                resource.AddService(
                    serviceName,
                    // The namespace is what groups `api` and `auth` as one product in a backend
                    // that sees more than this stack.
                    serviceNamespace: "mystack",
                    serviceVersion: ApplicationVersion(builder.Environment)
                )
            )
            .WithTracing(tracing =>
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                        // Health probes are polled forever; their spans would be most of the
                        // trace volume while answering nothing a probe log couldn't.
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(
                                "/health",
                                StringComparison.OrdinalIgnoreCase
                            )
                    )
                    .AddHttpClientInstrumentation()
                    // Npgsql publishes its own ActivitySource and Meter, so database work needs a
                    // name subscribed, not a package.
                    .AddSource("Npgsql")
            )
            .WithMetrics(metrics =>
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Npgsql")
            )
            .WithLogging(
                configureBuilder: null,
                configureOptions: options =>
                {
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes = true;
                }
            );

        // Only export when somewhere to export to is configured — otherwise the exporter spends
        // its retry budget against a dead endpoint on every bare `dotnet run`. The standard OTel
        // variable is the switch, so turning telemetry on is deployment configuration, not code.
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        builder.Services.AddHostedService<TelemetryBootstrap>();

        return builder;
    }

    /// <summary>
    /// Emits <c>act.sub</c> onto the request span and the log scope when the authenticated
    /// principal carries an <c>act</c> claim. Register it after authentication — before it, there
    /// is no principal to read.
    /// </summary>
    public static IApplicationBuilder UseActorEnrichment(this IApplicationBuilder app) =>
        app.UseMiddleware<ActorEnrichmentMiddleware>();

    private static string? ApplicationVersion(IHostEnvironment environment)
    {
        // The application's assembly, not the entry assembly: under WebApplicationFactory the
        // entry assembly is the test host, while ApplicationName stays the app under test.
        try
        {
            return Assembly
                .Load(new AssemblyName(environment.ApplicationName))
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }
}
