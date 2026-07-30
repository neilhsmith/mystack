using Microsoft.AspNetCore.HttpLogging;

namespace MyStack.Observability;

// Health probes are polled forever; their envelopes would be most of the log volume while
// answering nothing — the same reasoning as the trace filter in ObservabilityExtensions.
internal sealed class HealthRequestLoggingSuppressor : IHttpLoggingInterceptor
{
    public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext)
    {
        if (
            logContext.HttpContext.Request.Path.StartsWithSegments(
                "/health",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            logContext.LoggingFields = HttpLoggingFields.None;
        }

        return default;
    }

    public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext) => default;
}
