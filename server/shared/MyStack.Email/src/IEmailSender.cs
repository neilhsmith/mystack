namespace MyStack.Email;

/// <summary>Delivers a transactional email. The seam callers depend on so the transport (SMTP
/// today, a provider API if one ever earns it) stays an implementation detail. A failed send
/// throws — in the worker that rides the queue's retry-then-dead-letter policy.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
