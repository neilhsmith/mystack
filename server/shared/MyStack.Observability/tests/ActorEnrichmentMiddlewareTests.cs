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

        await middleware.InvokeAsync(ContextFor(new Claim("act", """{"sub":"admin-1"}""")));

        activity.GetTagItem("act.sub").ShouldBe("admin-1");
        var scope = logger
            .Scopes.ShouldHaveSingleItem()
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>();
        scope!["act.sub"].ShouldBe("admin-1");
    }

    [Fact]
    public async Task Invoke_TagsTheSubject_ForAnyAuthenticatedPrincipal()
    {
        using var activity = new Activity("request").Start();
        var logger = new ScopeRecordingLogger();
        var middleware = new ActorEnrichmentMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(ContextFor(new Claim("sub", "user-7")));

        activity.GetTagItem("sub").ShouldBe("user-7");
        activity.GetTagItem("act.sub").ShouldBeNull();
        var scope = logger
            .Scopes.ShouldHaveSingleItem()
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>();
        scope!["sub"].ShouldBe("user-7");
    }

    // The impersonation shape: one scope carrying both, so every log line names the pair.
    [Fact]
    public async Task Invoke_TagsSubjectAndActorTogether_WhenImpersonating()
    {
        using var activity = new Activity("request").Start();
        var logger = new ScopeRecordingLogger();
        var middleware = new ActorEnrichmentMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(
            ContextFor(new Claim("sub", "user-7"), new Claim("act", """{"sub":"admin-1"}"""))
        );

        activity.GetTagItem("sub").ShouldBe("user-7");
        activity.GetTagItem("act.sub").ShouldBe("admin-1");
        var scope = logger
            .Scopes.ShouldHaveSingleItem()
            .ShouldBeAssignableTo<IReadOnlyDictionary<string, object>>();
        scope!["sub"].ShouldBe("user-7");
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

    // A sub claim on an unauthenticated identity is noise, not identity — nothing is emitted.
    [Fact]
    public async Task Invoke_IgnoresClaims_OnAnUnauthenticatedPrincipal()
    {
        using var activity = new Activity("request").Start();
        var logger = new ScopeRecordingLogger();
        var middleware = new ActorEnrichmentMiddleware(_ => Task.CompletedTask, logger);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim("sub", "user-7")], authenticationType: null)
            ),
        };
        await middleware.InvokeAsync(context);

        activity.GetTagItem("sub").ShouldBeNull();
        logger.Scopes.ShouldBeEmpty();
    }

    private static DefaultHttpContext ContextFor(params Claim[] claims) =>
        new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")),
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
