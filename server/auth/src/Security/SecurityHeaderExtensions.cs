namespace MyStack.Auth.Security;

internal static class SecurityHeaderExtensions
{
    // NetEscapades.AspNetCore.SecurityHeaders' API baseline, tightened where an authorization
    // server can afford to be. The library owns the parts that drift as browsers move — the
    // Permissions-Policy deny list, HSTS mechanics (https-only, localhost excluded) — so none of
    // that is maintained by hand here.
    public static IApplicationBuilder UseAuthSecurityHeaders(this IApplicationBuilder app) =>
        app.UseSecurityHeaders(policy =>
            policy
                .AddDefaultApiSecurityHeaders()
                // The baseline CSP stops at default-src/frame-ancestors. A host that serves no
                // HTML can also pin the two document-level escape hatches shut; the sign-in page
                // has to loosen this deliberately, which is the right way round for the one
                // deployable that holds credentials.
                .AddContentSecurityPolicy(csp =>
                {
                    csp.AddDefaultSrc().None();
                    csp.AddFrameAncestors().None();
                    csp.AddBaseUri().None();
                    csp.AddFormAction().None();
                })
                // Baseline is same-site, which lets sibling subdomains embed responses. Nothing
                // legitimate embeds an auth response — OIDC traffic is navigation, not embedding.
                .AddCrossOriginResourcePolicy(resource => resource.SameOrigin())
                // Baseline HSTS lacks includeSubDomains. Still no preload: that is a one-way door
                // onto a list shipped inside browsers — an operator's decision, not a default.
                .AddStrictTransportSecurityMaxAgeIncludeSubDomains()
        );
}
