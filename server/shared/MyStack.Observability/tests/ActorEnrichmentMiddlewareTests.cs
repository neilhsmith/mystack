using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace MyStack.Observability.Tests;

public sealed class ActorEnrichmentMiddlewareTests
{
    [Fact]
    public async Task Invoke_TagsTheSpanAndTheLogScope_WhenAnActorIsPresent()
    {
        using var activity = new Activity("request").Start();
        var logger = new ScopeRecordingLogger();
        var middleware = new ActorEnrichmentMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(ContextFor("""{"sub":"admin-1"}"""));

        activity.GetTagItem("act.sub").ShouldBe("admin-1");
        var scope = logger
            .Scopes.ShouldHaveSingleItem()
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>();
        scope!["act.sub"].ShouldBe("admin-1");
    }

    [Fact]
    public async Task Invoke_EmitsNothing_WithoutAnActor()
    {
        using var activity = new Activity("request").Start();
        var logger = new ScopeRecordingLogger();
        var middleware = new ActorEnrichmentMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(new DefaultHttpContext());

        activity.GetTagItem("act.sub").ShouldBeNull();
        logger.Scopes.ShouldBeEmpty();
    }

    private static DefaultHttpContext ContextFor(string act) =>
        new()
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("act", act)], authenticationType: "test")
            ),
        };

    private sealed class ScopeRecordingLogger : ILogger<ActorEnrichmentMiddleware>
    {
        public List<object?> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            Scopes.Add(state);
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) { }
    }
}
