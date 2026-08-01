namespace MyStack.Auth.Security;

internal static class SecurityHeaderExtensions
{
    // Razor pages carry this named policy; everything else gets the default.
    public const string PagesPolicy = "pages";

    // NetEscapades.AspNetCore.SecurityHeaders' API baseline, tightened where an authorization
    // server can afford to be. The library owns the parts that drift as browsers move — the
    // Permissions-Policy deny list, HSTS mechanics (https-only, localhost excluded) — so none of
    // that is maintained by hand here.
    public static IServiceCollection AddAuthSecurityHeaders(this IServiceCollection services)
    {
        services
            .AddSecurityHeaderPolicies()
            .SetDefaultPolicy(policy => AddAuthPolicy(policy, selfFormAction: false))
            // Two page-only deltas: form-action 'self' (a rendered form has to post back) and
            // Cache-Control: no-store (a rendered page holds credentials). Everything else stays
            // pinned; the design pass opens style-src if and when there is styling to allow.
            .AddPolicy(PagesPolicy, policy => AddAuthPolicy(policy, selfFormAction: true));

        return services;
    }

    public static IApplicationBuilder UseAuthSecurityHeaders(this IApplicationBuilder app) =>
        app.UseSecurityHeaders();

    private static void AddAuthPolicy(HeaderPolicyCollection policy, bool selfFormAction)
    {
        policy
            .AddDefaultApiSecurityHeaders()
            // The baseline CSP stops at default-src/frame-ancestors. Endpoints that serve no
            // HTML can also pin the two document-level escape hatches shut; only the pages that
            // hold credentials loosen form-action, which is the right way round.
            .AddContentSecurityPolicy(csp =>
            {
                csp.AddDefaultSrc().None();
                csp.AddFrameAncestors().None();
                csp.AddBaseUri().None();

                var formAction = csp.AddFormAction();
                if (selfFormAction)
                {
                    formAction.Self();
                }
                else
                {
                    formAction.None();
                }
            })
            // Baseline is same-site, which lets sibling subdomains embed responses. Nothing
            // legitimate embeds an auth response — OIDC traffic is navigation, not embedding.
            .AddCrossOriginResourcePolicy(resource => resource.SameOrigin())
            // Baseline HSTS lacks includeSubDomains. Still no preload: that is a one-way door
            // onto a list shipped inside browsers — an operator's decision, not a default.
            .AddStrictTransportSecurityMaxAgeIncludeSubDomains();

        if (selfFormAction)
        {
            // Rendered pages hold credentials, codes and reset forms; bfcache, history and any
            // shared cache must not be able to re-show one after sign-out. The default policy
            // deliberately doesn't carry this: discovery and the JWKS are meant to be cached.
            policy.AddCustomHeader("Cache-Control", "no-store");
        }
    }
}
