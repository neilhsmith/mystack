using System.Text.Json;
using System.Text.RegularExpressions;
using MyStack.Email;
using RabbitMQ.Client;
using Shouldly;

namespace MyStack.Auth.Tests;

// auth's half of the email contract, read straight off the broker: a SendEmail command lands on
// the queue the worker consumes. The delivery half — queue to inbox — is proven in server/worker's
// own suite, so together the two suites close the loop without either hosting the other.
internal static partial class WorkerQueue
{
    private const string QueueName = "worker";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<SendEmail> WaitForEmailAsync(
        AuthAppFixture app,
        string toAddress,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await ConnectAsync(app, cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: cancellationToken
        );

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var delivery = await channel.BasicGetAsync(
                QueueName,
                autoAck: false,
                cancellationToken
            );
            if (delivery is null)
            {
                await Task.Delay(200, cancellationToken);
                continue;
            }

            // Non-matching messages are earlier tests' leftovers (the assembly runs serial), so
            // consuming them keeps the queue from silting up.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);

            var email = JsonSerializer.Deserialize<SendEmail>(delivery.Body.Span, Json);
            if (email is not null && email.Email.To.Any(to => to.Address == toAddress))
            {
                return email;
            }
        }

        throw new InvalidOperationException(
            $"No SendEmail for {toAddress} arrived on the worker queue."
        );
    }

    /// <summary>
    /// Proves the negative: nothing for this address is on the queue. Peeks without acking, so
    /// closing the channel requeues everything it looked at.
    /// </summary>
    public static async Task EnsureNoEmailAsync(
        AuthAppFixture app,
        string toAddress,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await ConnectAsync(app, cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            cancellationToken: cancellationToken
        );

        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var delivery = await channel.BasicGetAsync(
                QueueName,
                autoAck: false,
                cancellationToken
            );
            if (delivery is null)
            {
                await Task.Delay(200, cancellationToken);
                continue;
            }

            var email = JsonSerializer.Deserialize<SendEmail>(delivery.Body.Span, Json);
            if (email is not null && email.Email.To.Any(to => to.Address == toAddress))
            {
                throw new InvalidOperationException(
                    $"A SendEmail for {toAddress} was published, but none was expected."
                );
            }
        }
    }

    public static (string Path, string UserId, string Token) ParseAccountLink(this SendEmail email)
    {
        var text = email.Email.TextBody;
        text.ShouldNotBeNull();

        var match = LinkRegex().Match(text);
        match.Success.ShouldBeTrue($"no link found in email body: {text}");

        var uri = new Uri(match.Value);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

        return (
            uri.PathAndQuery,
            query["userId"].ShouldNotBeNull(),
            query["token"].ShouldNotBeNull()
        );
    }

    private static async Task<IConnection> ConnectAsync(
        AuthAppFixture app,
        CancellationToken cancellationToken
    )
    {
        var factory = new ConnectionFactory { Uri = new Uri(app.BrokerConnectionString) };
        return await factory.CreateConnectionAsync(cancellationToken);
    }

    [GeneratedRegex(@"https?://[^\s""<]+")]
    private static partial Regex LinkRegex();
}
