using Microsoft.AspNetCore.Builder;

namespace Api.Authorization;

/// <summary>
/// Convenience extensions for declaring scope + permission requirements on endpoints.
/// <para>
/// Reading the resulting endpoint chain top-to-bottom should describe the auth contract
/// without the reader having to know the policy-name encoding:
/// <code>
/// group.MapGet("/", GetAll)
///     .RequireScope(Scopes.Read)
///     .RequirePermission(Permissions.Posts.Read);
/// </code>
/// </para>
/// </summary>
public static class AuthorizationEndpointExtensions
{
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.RequireAuthorization(DynamicAuthorizationPolicyProvider.ScopePrefix + scope);
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.RequireAuthorization(DynamicAuthorizationPolicyProvider.PermissionPrefix + permission);
    }
}
