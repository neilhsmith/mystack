namespace MyStack.Messaging;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>
    /// Seconds between redelivery attempts after a handler throws, one entry per retry — [1, 5,
    /// 30] when unset. Past the last one the message moves to the dead-letter queue, where it
    /// waits for a human. Tests set [0] so the retry-then-dead-letter sequence is provable in
    /// seconds. Null rather than a populated default because the configuration binder merges
    /// into an existing array by index — a shorter configured schedule would keep the default's
    /// tail.
    /// </summary>
    public double[]? RetryCooldownsInSeconds { get; set; }
}
