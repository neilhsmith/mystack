namespace MyStack.Auth.Messaging;

/// <summary>
/// Published daily by <see cref="PruneScheduler"/> and consumed by auth itself — pruning touches
/// auth's own tables, so no other deployable may handle it.
/// </summary>
public sealed record PruneOidcTokens;
