using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;

namespace MyStack.Email.Tests;

// The email.sends counter, proven against fakes — the unit level auth-track step 7 asks for.
public sealed class MeteredSenderTests
{
    [Fact]
    public async Task A_successful_send_counts_as_sent()
    {
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var sends = Collector(meterFactory);

        var inner = new RecordingSender();
        var sender = new MeteredEmailSender(inner, new EmailMetrics(meterFactory));

        var message = Message();
        await sender.SendAsync(message, TestContext.Current.CancellationToken);

        inner.Sent.ShouldBe(message);
        var measurement = sends.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tags["outcome"].ShouldBe(EmailMetrics.Sent);
    }

    [Fact]
    public async Task A_failed_send_counts_as_failed_and_rethrows_unchanged()
    {
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = services.GetRequiredService<IMeterFactory>();
        using var sends = Collector(meterFactory);

        var sender = new MeteredEmailSender(new ExplodingSender(), new EmailMetrics(meterFactory));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            sender.SendAsync(Message(), TestContext.Current.CancellationToken)
        );

        thrown.Message.ShouldBe("Deliberate sender failure.");
        var measurement = sends.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["outcome"].ShouldBe(EmailMetrics.Failed);
    }

    private static MetricCollector<long> Collector(IMeterFactory meterFactory) =>
        new(meterFactory, EmailMetrics.MeterName, "email.sends");

    private static EmailMessage Message() =>
        new()
        {
            To = [new EmailAddress("person@mystack.test")],
            Subject = "Welcome",
            TextBody = "Hello.",
        };

    private sealed class RecordingSender : IEmailSender
    {
        public EmailMessage? Sent { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent = message;
            return Task.CompletedTask;
        }
    }

    private sealed class ExplodingSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Deliberate sender failure.");
    }
}
