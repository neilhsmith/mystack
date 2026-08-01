using MyStack.Auth.Data;
using MyStack.Contracts.Api;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Oidc;

internal static class OidcExtensions
{
    private const string TestingEnvironment = "Testing";

    public static WebApplicationBuilder AddAuthOpenIddict(this WebApplicationBuilder builder)
    {
        var lifetimes =
            builder.Configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>()
            ?? new OidcOptions();

        // The same section, through the options pipeline too: the one-shot read above feeds the
        // server builder before the container exists, and this registration makes
        // IOptions<OidcOptions> resolvable and fails the boot on a nonsense lifetime instead of
        // silently issuing dead-on-arrival tokens.
        builder
            .Services.AddOptions<OidcOptions>()
            .BindConfiguration(OidcOptions.SectionName)
            .Validate(
                options =>
                    options.AccessTokenLifetime > TimeSpan.Zero
                    && options.IdentityTokenLifetime > TimeSpan.Zero
                    && options.AuthorizationCodeLifetime > TimeSpan.Zero
                    && options.RefreshTokenLifetime > TimeSpan.Zero
                    && options.DeviceCodeLifetime > TimeSpan.Zero
                    && options.UserCodeLifetime > TimeSpan.Zero,
                "Every Oidc:* lifetime must be positive."
            )
            .ValidateOnStart();

        builder.Services.AddScoped<GrantMetricsHandler>();
        builder.Services.AddScoped<BackchannelLogoutNotifier>();

        // The bound on how long an unreachable client can hold up a user's sign-out: logout
        // notifications are delivered concurrently and best-effort, and this timeout is the
        // entire retry story (docs/auth.md records why there is no queue behind it).
        builder.Services.AddHttpClient(
            BackchannelLogoutNotifier.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(5)
        );

        builder
            .Services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
            .AddServer(server =>
            {
                server
                    .SetAuthorizationEndpointUris("connect/authorize")
                    .SetPushedAuthorizationEndpointUris("connect/par")
                    .SetTokenEndpointUris("connect/token")
                    .SetUserInfoEndpointUris("connect/userinfo")
                    .SetIntrospectionEndpointUris("connect/introspection")
                    .SetDeviceAuthorizationEndpointUris("connect/device")
                    .SetEndUserVerificationEndpointUris("connect/verify")
                    .SetEndSessionEndpointUris("connect/endsession")
                    .SetRevocationEndpointUris("connect/revocation");

                // Authorization code with refresh tokens for humans, client credentials for
                // machines, the device grant for clients without a browser or keyboard — and
                // nothing else. No password grant, in any environment (architecture §3). PKCE is
                // a per-client requirement the seeder declares — mandatory for public clients,
                // optional-but-validated for confidential ones, the RFC 9700 split (their nonce
                // covers code injection) — like PAR's per-client opt-in below.
                server.AllowAuthorizationCodeFlow();
                server.AllowRefreshTokenFlow();
                server.AllowClientCredentialsFlow();
                server.AllowDeviceAuthorizationFlow();

                server.RegisterScopes(
                    Scopes.Email,
                    Scopes.Profile,
                    Scopes.Roles,
                    ApiScopes.Read,
                    ApiScopes.Write
                );

                server
                    .SetAccessTokenLifetime(lifetimes.AccessTokenLifetime)
                    .SetIdentityTokenLifetime(lifetimes.IdentityTokenLifetime)
                    .SetAuthorizationCodeLifetime(lifetimes.AuthorizationCodeLifetime)
                    .SetRefreshTokenLifetime(lifetimes.RefreshTokenLifetime)
                    .SetDeviceCodeLifetime(lifetimes.DeviceCodeLifetime)
                    .SetUserCodeLifetime(lifetimes.UserCodeLifetime);

                // The refresh horizon is absolute. OpenIddict's default slides the window
                // forward on every rotation, so a session refreshing at least fortnightly would
                // never re-authenticate — docs/auth.md sells the 14 days as a hard ceiling, and
                // this is what makes that true.
                server.DisableSlidingRefreshTokenExpiration();

                // Two default sets trimmed to what this server actually honors: `plain` PKCE is
                // challenge == verifier — none of the interception protection PKCE exists for,
                // and the one OAuth 2.1 deviation the review found — and discovery advertised
                // prompt values (`consent`, `select_account`) nothing here implements: there is
                // no consent screen (D17) and one cookie session to select from.
                server.Configure(options =>
                {
                    options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                    options.PromptValues.Remove(PromptValues.Consent);
                    options.PromptValues.Remove(PromptValues.SelectAccount);
                });

                // `server/api` validates access tokens as plain JWTs against the discovery
                // document; encrypted tokens would force it onto auth's key material instead.
                server.DisableAccessTokenEncryption();

                if (builder.Environment.IsDevelopment())
                {
                    server.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
                }
                else if (builder.Environment.IsEnvironment(TestingEnvironment))
                {
                    // In-memory keys: the test host must not write to a CI runner's cert store.
                    server.AddEphemeralEncryptionKey().AddEphemeralSigningKey();

                    // The default 30-second reuse leeway exists so a client's concurrent
                    // refreshes don't trip reuse detection; zeroed here so the
                    // replay-after-rotation test observes the revocation immediately.
                    server.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                }
                // Anywhere else key material has to be configured deliberately, and OpenIddict
                // refuses to boot without it — recorded in docs/auth.md's hardening items.

                var aspnetcore = server
                    .UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndUserVerificationEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    // Interactive-endpoint rejections (a bad client_id, an unregistered
                    // redirect) defer to the status-code pages middleware instead of
                    // OpenIddict's own bare error page, so a browser stranded mid-flow gets the
                    // same error page as everything else. Token endpoint errors stay the OAuth
                    // JSON the spec requires.
                    .EnableStatusCodePagesIntegration();

                if (
                    builder.Environment.IsDevelopment()
                    || builder.Environment.IsEnvironment(TestingEnvironment)
                )
                {
                    aspnetcore.DisableTransportSecurityRequirement();
                }

                server.AddEventHandler<OpenIddictServerEvents.ApplyTokenResponseContext>(
                    descriptor => descriptor.UseScopedHandler<GrantMetricsHandler>()
                );
            });

        return builder;
    }
}
