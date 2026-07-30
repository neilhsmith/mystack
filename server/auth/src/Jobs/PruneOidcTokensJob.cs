using MyStack.Jobs;
using OpenIddict.Abstractions;

namespace MyStack.Auth.Jobs;

/// <summary>
/// oidc_tokens and oidc_authorizations gain rows on every sign-in and OpenIddict never deletes
/// them on its own — without this the tables grow forever.
/// </summary>
internal sealed class PruneOidcTokensJob(
    IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations
) : IRecurringJob
{
    public const string Id = "prune-oidc-tokens";

    // Prune only entries this much older than their creation — comfortably past every configured
    // lifetime (refresh tokens are the longest at 14 days), and PruneAsync itself only ever
    // removes entries that are already expired or no longer valid.
    private static readonly TimeSpan RetainFor = TimeSpan.FromDays(30);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - RetainFor;

        // Tokens first: an authorization is only prunable once its tokens are gone.
        await tokens.PruneAsync(threshold, cancellationToken);
        await authorizations.PruneAsync(threshold, cancellationToken);
    }
}
