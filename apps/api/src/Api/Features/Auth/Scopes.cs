namespace Api.Features.Auth;

/// <summary>
/// Custom OAuth scopes the auth server issues and the API enforces. Standard OIDC scopes
/// (<c>openid</c>, <c>profile</c>, <c>email</c>, <c>offline_access</c>) are not listed
/// here — those come from <c>OpenIddictConstants.Scopes</c> directly and are registered
/// via OpenIddict's <c>RegisterScopes(...)</c> in <c>Program.cs</c>.
/// <para>
/// Format is <c>{product}.{verb}</c>; mystack uses two coarse scopes for now —
/// <see cref="Read"/> for safe GETs and <see cref="Write"/> for everything that mutates
/// state. Permissions are the fine-grained check (action-level); scopes are the broad
/// gate ("can this client *category* of action at all"), allowing a downstream BFF or
/// CLI to request only the surface it needs.
/// </para>
/// </summary>
public static class Scopes
{
    public const string Read = "mystack.read";
    public const string Write = "mystack.write";

    /// <summary>
    /// Every custom scope name + a one-line display description. Used by the
    /// OpenIddict scope seeder so the spec at <c>/.well-known/openid-configuration</c>
    /// advertises them.
    /// </summary>
    public static IReadOnlyList<(string Name, string Description)> Catalog { get; } =
    [
        (Read, "Read access to the mystack API."),
        (Write, "Write access to the mystack API."),
    ];
}
