using Cronos;

namespace MyStack.Messaging;

/// <summary>
/// One declared schedule: publish what <see cref="Factory"/> produces at every occurrence of
/// <see cref="Expression"/>. Public so a host (or its tests) can enumerate what it scheduled;
/// created only through <c>AddScheduledMessage</c>, which is where the cron string is validated.
/// </summary>
public sealed record ScheduledMessage(
    string Cron,
    CronExpression Expression,
    Func<object> Factory,
    string MessageType
);
