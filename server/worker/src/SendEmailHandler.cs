using MyStack.Email;

namespace MyStack.Worker;

/// <summary>
/// The worker's first real consumer (architecture §3.3): any app publishes <see cref="SendEmail"/>
/// to the worker's queue, and this delivers it over SMTP. A failed send throws on purpose — the
/// queue's retry-then-dead-letter policy owns recovery, and the metered sender records every
/// attempt's outcome.
/// </summary>
public static class SendEmailHandler
{
    public static Task Handle(
        SendEmail command,
        IEmailSender email,
        CancellationToken cancellationToken
    ) => email.SendAsync(command.Email, cancellationToken);
}
