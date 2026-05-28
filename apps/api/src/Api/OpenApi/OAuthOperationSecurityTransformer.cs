using Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Api.OpenApi;

/// <summary>
/// Operation-level OpenAPI transformer that stamps a per-endpoint security requirement
/// onto every operation that requires authentication. Two effects:
/// <list type="bullet">
///   <item>Swagger UI shows the lock icon next to protected operations.</item>
///   <item>"Try it out" sends the bearer token (acquired via the global Authorize button)
///   only on operations that actually need it.</item>
/// </list>
/// <para>
/// Scopes attached to the security requirement come from the endpoint's
/// <see cref="DynamicAuthorizationPolicyProvider.ScopePrefix"/>-prefixed authorize
/// policies — i.e. exactly the scopes the request will need to satisfy
/// <see cref="ScopeAuthorizationHandler"/>. Permission policies don't translate to OAuth
/// scopes (they're an API-internal concept) so they're ignored here.
/// </para>
/// </summary>
internal sealed class OAuthOperationSecurityTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        // AllowAnonymous wins — if anything in the endpoint chain says "no auth",
        // don't stamp a security requirement (matches what UseAuthorization does).
        var allowsAnonymous = false;
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var requiresAuth = false;

        foreach (var item in metadata)
        {
            switch (item)
            {
                case IAllowAnonymous:
                    allowsAnonymous = true;
                    break;

                case IAuthorizeData authorize:
                    requiresAuth = true;
                    if (!string.IsNullOrEmpty(authorize.Policy)
                        && authorize.Policy.StartsWith(DynamicAuthorizationPolicyProvider.ScopePrefix, StringComparison.Ordinal))
                    {
                        scopes.Add(authorize.Policy[DynamicAuthorizationPolicyProvider.ScopePrefix.Length..]);
                    }
                    break;
            }
        }

        if (allowsAnonymous || !requiresAuth)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            // Reference resolves against components.securitySchemes[SchemeName]
            // (declared by OAuthSecuritySchemeTransformer). The hostDocument arg is
            // required so the serializer can resolve the reference at write time —
            // without it the requirement serialises as `{}` (no scheme name written).
            [new OpenApiSecuritySchemeReference(
                OAuthSecuritySchemeTransformer.SchemeName,
                context.Document,
                externalResource: null)] = scopes.ToList(),
        });

        return Task.CompletedTask;
    }
}
