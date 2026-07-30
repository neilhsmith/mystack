using System.Text.Json;
using Shouldly;

namespace MyStack.Auth.Tests;

public sealed class ProblemDetailsTests(AuthAppFixture app)
{
    [Fact]
    public async Task UnhandledException_AnswersProblemDetails_WithoutLeakingTheException()
    {
        var response = await app.Client.GetAsync(
            "/debug/throw",
            TestContext.Current.CancellationToken
        );

        ((int)response.StatusCode).ShouldBe(500);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(body).RootElement;

        problem.GetProperty("status").GetInt32().ShouldBe(500);
        problem.GetProperty("title").GetString().ShouldNotBeNullOrEmpty();
        // The trace id is what connects this response to its span and log lines.
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();

        // The message stays in the logs; an unauthenticated response never carries it.
        body.ShouldNotContain("Deliberate test failure");
    }
}
