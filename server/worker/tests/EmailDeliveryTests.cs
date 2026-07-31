using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyStack.Email;
using Shouldly;
using Wolverine;

namespace MyStack.Worker.Tests;

// The real contract, both ends (architecture §3.3): SendEmail published to the worker's queue —
// the same address a producing app will use — handled by the worker, delivered over SMTP, and
// read back from Mailpit's inbox through its REST API.
public sealed class EmailDeliveryTests(WorkerAppFixture app)
{
    [Fact]
    public async Task A_published_send_email_lands_in_the_inbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var subject = $"delivery-{Guid.NewGuid():N}";

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            await bus.EndpointFor(new Uri("rabbitmq://queue/worker"))
                .SendAsync(
                    new SendEmail(
                        new EmailMessage
                        {
                            To = [new EmailAddress("person@mystack.test", "Person")],
                            Subject = subject,
                            HtmlBody = "<p>Hello from the worker.</p>",
                            TextBody = "Hello from the worker.",
                        }
                    )
                );
        }

        using var api = new HttpClient { BaseAddress = app.MailpitApiBaseAddress };
        var message = await FindDeliveredAsync(api, subject, cancellationToken);

        message
            .GetProperty("To")[0]
            .GetProperty("Address")
            .GetString()
            .ShouldBe("person@mystack.test");
        message
            .GetProperty("From")
            .GetProperty("Address")
            .GetString()
            .ShouldBe("no-reply@mystack.test");
        message
            .GetProperty("HTML")
            .GetString()
            .ShouldNotBeNull()
            .ShouldContain("Hello from the worker.");
        message
            .GetProperty("Text")
            .GetString()
            .ShouldNotBeNull()
            .ShouldContain("Hello from the worker.");
    }

    private static async Task<JsonElement> FindDeliveredAsync(
        HttpClient api,
        string subject,
        CancellationToken cancellationToken
    )
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            var found = await api.GetFromJsonAsync<JsonElement>(
                $"/api/v1/search?query=subject:%22{subject}%22",
                cancellationToken
            );

            if (found.GetProperty("messages").GetArrayLength() == 1)
            {
                var id = found.GetProperty("messages")[0].GetProperty("ID").GetString();
                return await api.GetFromJsonAsync<JsonElement>(
                    $"/api/v1/message/{id}",
                    cancellationToken
                );
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("the worker should deliver the email to Mailpit");
    }
}
