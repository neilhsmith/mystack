using OpenIddict.Abstractions;

namespace MyStack.Auth.Messaging;

/// <summary>
/// oidc_tokens and oidc_authorizations gain rows on every sign-in and OpenIddict never deletes
/// them on its own — without this the tables grow forever.
/// </summary>
public static partial class PruneOidcTokensHandler
{
    // Prune only entries this much older than their creation — comfortably past every configured
    // lifetime (refresh tokens are the longest at 14 days), and PruneAsync itself only ever
    // removes entries that are already expired or no longer valid.
    private static readonly TimeSpan RetainFor = TimeSpan.FromDays(30);

    public static async Task Handle(
        PruneOidcTokens message,
        IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations,
        ILogger<PruneOidcTokens> logger,
        CancellationToken cancellationToken
    )
    {
        var threshold = DateTimeOffset.UtcNow - RetainFor;

        // Tokens first: an authorization is only prunable once its tokens are gone.
        var prunedTokens = await tokens.PruneAsync(threshold, cancellationToken);
        var prunedAuthorizations = await authorizations.PruneAsync(threshold, cancellationToken);

        // The counts are the job's only observable outcome — without them "ran and pruned
        // nothing forever" is indistinguishable from working.
        LogPruned(logger, prunedTokens, prunedAuthorizations);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Pruned {Tokens} expired tokens and {Authorizations} expired authorizations."
    )]
    private static partial void LogPruned(ILogger logger, long tokens, long authorizations);
}
