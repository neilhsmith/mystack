namespace MyStack.Email;

/// <summary>Decorates the concrete sender so every attempt lands in <see cref="EmailMetrics"/> —
/// each retry of a failing send counts, which is what makes the failure rate honest. The failure
/// is counted and rethrown unchanged; recovery belongs to the caller's retry policy.</summary>
internal sealed class MeteredEmailSender(IEmailSender inner, EmailMetrics metrics) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await inner.SendAsync(message, cancellationToken);
        }
        catch
        {
            metrics.Record(EmailMetrics.Failed);
            throw;
        }

        metrics.Record(EmailMetrics.Sent);
    }
}
