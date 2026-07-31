namespace MyStack.Email;

/// <summary>
/// Renders one kind of email from its model. The bodies are plain interpolated strings for now
/// (architecture §3.3); this seam is what keeps that an implementation detail, so a designed
/// emails package can replace the markup later without touching a caller.
/// </summary>
public interface IEmailRenderer<in TModel>
{
    EmailContent Render(TModel model);
}

/// <summary>A rendered email: subject plus both bodies. Both are required — every email ships
/// multipart/alternative so non-HTML clients still render something.</summary>
public sealed record EmailContent(string Subject, string HtmlBody, string TextBody)
{
    /// <summary>The common case: this content, addressed to a single recipient.</summary>
    public EmailMessage ToMessage(EmailAddress to) =>
        new()
        {
            To = [to],
            Subject = Subject,
            HtmlBody = HtmlBody,
            TextBody = TextBody,
        };
}
