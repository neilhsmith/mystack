using Api.Features.Auth;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OpenIddictScopes = OpenIddict.Abstractions.OpenIddictConstants.Scopes;

namespace Api.OpenApi;

/// <summary>
/// Document-level OpenAPI transformer that declares the <c>oauth2</c> security scheme so
/// generated specs (and the Swagger UI rendering them) know how to drive a login.
/// <para>
/// Emits an Authorization Code + PKCE flow pointed at the in-process OpenIddict
/// endpoints (<c>/connect/authorize</c>, <c>/connect/token</c>) with the union of
/// standard OIDC scopes plus our custom <c>mystack.read</c> / <c>mystack.write</c>.
/// Operations get per-endpoint security requirements via
/// <see cref="OAuthOperationSecurityTransformer"/> — this transformer only owns the
/// scheme definition.
/// </para>
/// </summary>
internal sealed class OAuthSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// The OpenAPI security-scheme name. Operations reference it by this key; Swagger UI's
    /// <c>OAuthClientId</c> setting binds against the same scheme name.
    /// </summary>
    public const string SchemeName = "oauth2";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = "OAuth 2.0 Authorization Code flow with PKCE. " +
                "Authenticate via /connect/authorize against the in-process OpenIddict server.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    // Relative URLs — Swagger UI resolves them against the page origin so
                    // the same spec works whether the API is served from localhost, a
                    // staging URL, or behind a reverse proxy.
                    AuthorizationUrl = new Uri("/connect/authorize", UriKind.Relative),
                    TokenUrl = new Uri("/connect/token", UriKind.Relative),
                    RefreshUrl = new Uri("/connect/token", UriKind.Relative),
                    Scopes = new Dictionary<string, string>
                    {
                        [OpenIddictScopes.OpenId] = "OpenID — required for OIDC.",
                        [OpenIddictScopes.Profile] = "Read profile claims (name, preferred_username).",
                        [OpenIddictScopes.Email] = "Read email + email_verified.",
                        [OpenIddictScopes.Roles] = "Read role claim.",
                        [OpenIddictScopes.OfflineAccess] = "Issue a refresh token alongside the access token.",
                        [Scopes.Read] = "Read access to the mystack API.",
                        [Scopes.Write] = "Write access to the mystack API.",
                    },
                },
            },
        };

        return Task.CompletedTask;
    }
}
