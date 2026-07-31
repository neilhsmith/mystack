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

    // The cross-device window: how long a device's codes stay redeemable while the user walks to
    // a real browser, signs in and approves. One window for both — a user code that outlives its
    // device code (or the reverse) is only a confusing way to fail.
    public TimeSpan DeviceCodeLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan UserCodeLifetime { get; init; } = TimeSpan.FromMinutes(15);
}
