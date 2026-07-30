using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Messaging;
using OpenIddict.Abstractions;
using Shouldly;
using Wolverine;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Tests;

public sealed class MessagingTests(AuthAppFixture app)
{
    // The real maintenance flow, end to end: the message rides the broker to auth's own queue
    // and its handler prunes — proven by the side effect, not by asserting Wolverine works.
    [Fact]
    public async Task PublishedPrune_RemovesLongExpiredTokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var subject = $"prune-{Guid.NewGuid():N}";

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            await tokens.CreateAsync(
                new OpenIddictTokenDescriptor
                {
                    Subject = subject,
                    Status = Statuses.Valid,
                    Type = TokenTypeHints.AccessToken,
                    CreationDate = DateTimeOffset.UtcNow.AddDays(-40),
                    ExpirationDate = DateTimeOffset.UtcNow.AddDays(-39),
                },
                cancellationToken
            );
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.PublishAsync(new PruneOidcTokens());
        }

        await WaitUntilAsync(
            async () => await CountTokensAsync(subject, cancellationToken) == 0,
            "the published prune should remove the long-expired token",
            cancellationToken
        );
    }

    // The scheduler's only logic worth owning is the next-run arithmetic; the timer loop itself
    // is the framework's.
    [Theory]
    [InlineData("2026-07-30T01:30:00Z", "01:30:00")]
    [InlineData("2026-07-30T03:00:00Z", "1.00:00:00")]
    [InlineData("2026-07-30T17:45:00Z", "09:15:00")]
    public void Scheduler_WaitsUntilTheNextThreeAmUtc(string nowText, string expectedDelay)
    {
        var now = DateTimeOffset.Parse(
            nowText,
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal
        );

        PruneScheduler.DelayUntilNextRun(now).ShouldBe(TimeSpan.Parse(expectedDelay, null));
    }

    private async Task<int> CountTokensAsync(string subject, CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        var count = 0;
        await foreach (var _ in tokens.FindBySubjectAsync(subject, cancellationToken))
        {
            count++;
        }

        return count;
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        string because,
        CancellationToken cancellationToken
    )
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(because);
    }
}
