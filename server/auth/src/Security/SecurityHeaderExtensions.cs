namespace MyStack.Auth.Security;

internal static class SecurityHeaderExtensions
{
    // Auth serves no HTML yet, and the policy is written for that: the sign-in page has to loosen
    // it deliberately, which is the right way round for the one host that holds credentials.
    private const string ContentSecurityPolicy =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), "
        + "fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), "
        + "payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), "
        + "usb=(), xr-spatial-tracking=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(
            async (context, next) =>
            {
                var headers = context.Response.Headers;

                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers["Permissions-Policy"] = PermissionsPolicy;

                // Confirmation and reset links carry a single-use credential in the query string,
                // so nothing this host serves ever names where the request came from.
                headers["Referrer-Policy"] = "no-referrer";

                headers["X-Content-Type-Options"] = "nosniff";

                // frame-ancestors above supersedes this; it stays for browsers that only read the
                // older header, and costs one line to keep.
                headers["X-Frame-Options"] = "DENY";

                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";

                await next();
            }
        );
}
