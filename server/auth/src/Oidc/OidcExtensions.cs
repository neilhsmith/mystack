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

        builder.Services.AddScoped<GrantMetricsHandler>();

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

                // Authorization code + PKCE with refresh tokens for humans, client credentials
                // for machines, the device grant for clients without a browser or keyboard — and
                // nothing else. No password grant, in any environment (architecture §3); PKCE is
                // required globally rather than per client, so no future registration can
                // quietly opt out. PAR is deliberately not required globally the same way:
                // making it mandatory is a per-client requirement the seeder declares.
                server.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
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
                }
                // Anywhere else key material has to be configured deliberately, and OpenIddict
                // refuses to boot without it — recorded in docs/auth.md's hardening items.

                var aspnetcore = server
                    .UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough()
                    .EnableEndUserVerificationEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();

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
