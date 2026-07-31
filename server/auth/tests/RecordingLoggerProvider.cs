using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MyStack.Auth.Tests;

// The console provider can't be read back; this one exists so tests can assert on what the host
// logged — the request-envelope lines in particular.
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, string Message)> entries = new();

    public IReadOnlyCollection<(string Category, string Message)> Entries => entries;

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, entries);

    public void Dispose() { }

    private sealed class RecordingLogger(
        string category,
        ConcurrentQueue<(string Category, string Message)> entries
    ) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            entries.Enqueue(
                (
                    category,
                    exception is null
                        ? formatter(state, exception)
                        : formatter(state, exception) + Environment.NewLine + exception
                )
            );
    }
}
