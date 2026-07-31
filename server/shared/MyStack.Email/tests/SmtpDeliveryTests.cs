using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shouldly;

namespace MyStack.Email.Tests;

// The integration level auth-track step 7 asks for: a real SMTP conversation with the Mailpit
// compose runs, read back through the same REST API the e2e suite will use.
public sealed class SmtpDeliveryTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    [Fact]
    public async Task Send_delivers_the_message_to_the_inbox()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sender = new SmtpEmailSender(
            Options.Create(
                new EmailOptions
                {
                    Host = mailpit.Host,
                    Port = mailpit.SmtpPort,
                    From = "no-reply@mystack.test",
                    FromName = "MyStack",
                }
            )
        );

        var subject = $"integration-{Guid.NewGuid():N}";
        await sender.SendAsync(
            new EmailMessage
            {
                To = [new EmailAddress("person@mystack.test", "Person")],
                Subject = subject,
                HtmlBody = "<p>Hello from the SMTP integration test.</p>",
                TextBody = "Hello from the SMTP integration test.",
            },
            cancellationToken
        );

        using var api = new HttpClient { BaseAddress = mailpit.ApiBaseAddress };
        var message = await api.GetFromJsonAsync<JsonElement>(
            "/api/v1/message/latest",
            cancellationToken
        );

        message.GetProperty("Subject").GetString().ShouldBe(subject);
        message
            .GetProperty("From")
            .GetProperty("Address")
            .GetString()
            .ShouldBe("no-reply@mystack.test");
        message.GetProperty("From").GetProperty("Name").GetString().ShouldBe("MyStack");
        message
            .GetProperty("To")[0]
            .GetProperty("Address")
            .GetString()
            .ShouldBe("person@mystack.test");
        message
            .GetProperty("HTML")
            .GetString()
            .ShouldNotBeNull()
            .ShouldContain("Hello from the SMTP integration test.");
        message
            .GetProperty("Text")
            .GetString()
            .ShouldNotBeNull()
            .ShouldContain("Hello from the SMTP integration test.");
    }
}
