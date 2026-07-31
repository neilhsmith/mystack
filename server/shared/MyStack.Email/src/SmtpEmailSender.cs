using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MyStack.Email;

/// <summary>The one concrete sender (architecture §3.3): MailKit over SMTP, in every environment.</summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var mime = ToMimeMessage(message, settings);

        // A connection per send: MailKit's SmtpClient is single-threaded and connection-oriented,
        // and at transactional volume the handshake is noise next to pooling's complexity.
        using var client = new SmtpClient();

        // Auto = implicit TLS on 465, STARTTLS wherever the server offers it — which is how the
        // same adapter speaks plaintext to Mailpit and TLS to a real provider on 587.
        await client.ConnectAsync(
            settings.Host,
            settings.Port,
            SecureSocketOptions.Auto,
            cancellationToken
        );

        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(
                settings.Username,
                settings.Password ?? "",
                cancellationToken
            );
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    // Internal and static so the mapping is provable without an SMTP server on the other end.
    internal static MimeMessage ToMimeMessage(EmailMessage message, EmailOptions settings)
    {
        // Caller bugs, not expected failures: an unroutable message should throw, retry and
        // dead-letter where an operator can see it — never be silently dropped.
        if (message.To.Count == 0)
        {
            throw new InvalidOperationException("An email needs at least one recipient.");
        }

        if (message.HtmlBody is null && message.TextBody is null)
        {
            throw new InvalidOperationException(
                "An email needs an HTML body, a text body, or both."
            );
        }

        var mime = new MimeMessage();

        var from = message.From ?? new EmailAddress(settings.From, settings.FromName);
        mime.From.Add(ToMailbox(from));

        foreach (var to in message.To)
        {
            mime.To.Add(ToMailbox(to));
        }

        foreach (var cc in message.Cc)
        {
            mime.Cc.Add(ToMailbox(cc));
        }

        foreach (var bcc in message.Bcc)
        {
            mime.Bcc.Add(ToMailbox(bcc));
        }

        if (message.ReplyTo is not null)
        {
            mime.ReplyTo.Add(ToMailbox(message.ReplyTo));
        }

        mime.Subject = message.Subject;

        var body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody };

        foreach (var attachment in message.Attachments)
        {
            body.Attachments.Add(
                attachment.FileName,
                attachment.Content.ToArray(),
                MimeKit.ContentType.Parse(attachment.ContentType)
            );
        }

        mime.Body = body.ToMessageBody();

        return mime;
    }

    private static MailboxAddress ToMailbox(EmailAddress address) =>
        new(address.Name, address.Address);
}
