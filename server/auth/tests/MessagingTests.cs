using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Auth.Messaging;
using MyStack.Messaging;
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
    public async Task PublishedPrune_RemovesLongExpiredTokens_AndOnlyThose()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var subject = $"prune-{Guid.NewGuid():N}";
        var survivorSubject = $"prune-survivor-{Guid.NewGuid():N}";

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

            // The survivor: live and recent. Without it, a handler that deleted *everything*
            // would pass the assert below just the same.
            await tokens.CreateAsync(
                new OpenIddictTokenDescriptor
                {
                    Subject = survivorSubject,
                    Status = Statuses.Valid,
                    Type = TokenTypeHints.AccessToken,
                    CreationDate = DateTimeOffset.UtcNow,
                    ExpirationDate = DateTimeOffset.UtcNow.AddHours(1),
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

        (await CountTokensAsync(survivorSubject, cancellationToken)).ShouldBe(1);
    }

    // The schedule itself: declared once in Program, enumerable here — the clock loop belongs to
    // MyStack.Messaging and is tested there.
    [Fact]
    public void PruneSchedule_IsDeclared()
    {
        var schedule = app.Services.GetServices<ScheduledMessage>().ShouldHaveSingleItem();

        schedule.MessageType.ShouldBe(nameof(PruneOidcTokens));
        schedule.Cron.ShouldBe("0 3 * * *");
        schedule.Factory().ShouldBeOfType<PruneOidcTokens>();
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
