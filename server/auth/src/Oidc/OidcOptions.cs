namespace MyStack.Auth.Oidc;

public sealed class OidcOptions
{
    public const string SectionName = "Oidc";

    // Fifteen minutes is architecture §3.1's number: the access-token lifetime is the bound on
    // how long a revoked permission override — or any role change — stays live in issued tokens.
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan IdentityTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan AuthorizationCodeLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(14);
}
