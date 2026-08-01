using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace MyStack.Auth.Security;

internal static class RateLimitExtensions
{
    public static WebApplicationBuilder AddAuthRateLimiter(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<RateLimitOptions>()
            .BindConfiguration(RateLimitOptions.SectionName)
            .Validate(
                options =>
                    options.WindowSeconds > 0
                    && options.SignIn > 0
                    && options.Register > 0
                    && options.ForgotPassword > 0
                    && options.ResendConfirmation > 0
                    && options.ChangePassword > 0
                    && options.Verify > 0,
                "Every RateLimiting:* value must be positive."
            )
            .ValidateOnStart();

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                // The honest hint for a well-behaved client; the body stays empty so the
                // status-code shaping downstream renders it per the caller's Accept header.
                context.HttpContext.Response.Headers.RetryAfter = Options(context.HttpContext)
                    .WindowSeconds.ToString();
                return ValueTask.CompletedTask;
            };

            // A global limiter rather than per-endpoint policies because the guarded unit is the
            // (method, path) pair: a Razor page is one endpoint for GET and POST alike, and only
            // the POSTs take credentials or drive email. The window is per instance and
            // in-memory — right for the single-VPS topology, revisit on replicas.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var permit = PermitFor(context);
                if (permit is null)
                {
                    return RateLimitPartition.GetNoLimiter("unlimited");
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{context.Request.Path.Value!.ToLowerInvariant()}|{ClientKey(context)}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permit.Value,
                        Window = TimeSpan.FromSeconds(Options(context).WindowSeconds),
                        QueueLimit = 0,
                    }
                );
            });
        });

        return builder;
    }

    private static int? PermitFor(HttpContext context)
    {
        var options = Options(context);
        var post = HttpMethods.IsPost(context.Request.Method);
        var path = context.Request.Path;

        // The verification page is limited on GET too: its entry form submits with GET, so the
        // device user-code space is probed with GETs.
        if (path.Equals("/connect/verify", StringComparison.OrdinalIgnoreCase))
        {
            return post || HttpMethods.IsGet(context.Request.Method) ? options.Verify : null;
        }

        if (!post)
        {
            return null;
        }

        return path switch
        {
            _ when path.Equals("/signin", StringComparison.OrdinalIgnoreCase) => options.SignIn,
            _ when path.Equals("/register", StringComparison.OrdinalIgnoreCase) => options.Register,
            _ when path.Equals("/forgot-password", StringComparison.OrdinalIgnoreCase) =>
                options.ForgotPassword,
            _ when path.Equals("/resend-confirmation", StringComparison.OrdinalIgnoreCase) =>
                options.ResendConfirmation,
            _ when path.Equals("/change-password", StringComparison.OrdinalIgnoreCase) =>
                options.ChangePassword,
            _ => null,
        };
    }

    // Kestrel always knows the peer; "unknown" only happens under TestServer, whose in-memory
    // connections carry no address (the Testing host maps a header onto it instead).
    private static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitOptions Options(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
}
