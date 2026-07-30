namespace MyStack.Auth.Messaging;

/// <summary>
/// Scheduled daily in Program via <c>AddScheduledMessage</c> and consumed by auth itself —
/// pruning touches auth's own tables, so no other deployable may handle it.
/// </summary>
public sealed record PruneOidcTokens;
