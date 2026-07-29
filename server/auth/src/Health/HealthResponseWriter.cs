using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MyStack.Auth.Health;

internal static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web
    );

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var payload = new Payload(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            [
                .. report.Entries.Select(entry => new Check(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    entry.Value.Duration.TotalMilliseconds,
                    Describe(entry.Value)
                )),
            ]
        );

        return context.Response.WriteAsJsonAsync(payload, SerializerOptions);
    }

    // These endpoints are unauthenticated, and a check that throws has its exception message
    // copied into Description by the framework — which is how a connection string reaches the
    // response body. Only descriptions a check wrote for itself are returned.
    private static string? Describe(HealthReportEntry entry) =>
        entry.Exception is null ? entry.Description : null;

    private sealed record Payload(string Status, double DurationMs, IReadOnlyList<Check> Checks);

    private sealed record Check(string Name, string Status, double DurationMs, string? Description);
}
