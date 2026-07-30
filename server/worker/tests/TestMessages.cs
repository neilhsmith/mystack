using System.Collections.Concurrent;

namespace MyStack.Worker.Tests;

/// <summary>
/// The observable side effect for pipeline tests — proof a handler actually ran (and how many
/// times), not just that the broker moved bytes.
/// </summary>
public sealed class MessageSink
{
    private readonly ConcurrentQueue<string> entries = new();

    public void Record(string value) => entries.Enqueue(value);

    public bool Contains(string value) => entries.Contains(value);

    public int Count(string value) => entries.Count(entry => entry == value);
}

// The worker's stand-in messages until MyStack.Email brings the first real contract. Handlers
// here are discovered only under the Testing environment (see the worker's Program).
public sealed record TestPing(string Value);

public static class TestPingHandler
{
    public static void Handle(TestPing ping, MessageSink sink) => sink.Record(ping.Value);
}

public sealed record TestExplosion(string Value);

public static class TestExplosionHandler
{
    public static void Handle(TestExplosion explosion, MessageSink sink)
    {
        // Recording before throwing is what lets a test count attempts.
        sink.Record(explosion.Value);
        throw new InvalidOperationException($"Deliberate handler failure for {explosion.Value}.");
    }
}
