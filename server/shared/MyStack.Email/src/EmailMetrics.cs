using System.Diagnostics.Metrics;

namespace MyStack.Email;

// The delivery-rate signal from architecture §3's metric table — the counter a provider outage
// moves first. Outcome values are a closed set, never derived from the message.
internal sealed class EmailMetrics
{
    public const string MeterName = "MyStack.Email";

    public const string Sent = "sent";
    public const string Failed = "failed";

    private readonly Counter<long> sends;

    public EmailMetrics(IMeterFactory meterFactory) =>
        sends = meterFactory
            .Create(MeterName)
            .CreateCounter<long>(
                "email.sends",
                unit: "{email}",
                description: "Email delivery attempts, by outcome."
            );

    public void Record(string outcome) =>
        sends.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
