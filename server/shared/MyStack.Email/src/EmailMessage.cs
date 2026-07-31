namespace MyStack.Email;

/// <summary>An email address, optionally with a display name. A bare string converts implicitly,
/// so callers can pass an address wherever an <see cref="EmailAddress"/> is expected.</summary>
public sealed record EmailAddress(string Address, string? Name = null)
{
    public static implicit operator EmailAddress(string address) => new(address);
}

/// <summary>A file attached to an email — raw bytes plus the metadata the adapter needs to encode
/// them for its transport.</summary>
public sealed record EmailAttachment(
    string FileName,
    string ContentType,
    ReadOnlyMemory<byte> Content
);

/// <summary>
/// A provider-agnostic transactional email — the shape a caller builds, publishes across the
/// broker inside <see cref="SendEmail"/>, and the worker hands to <see cref="IEmailSender"/>.
/// Nothing here is tied to SMTP; a later adapter maps the same shape onto its own transport.
/// </summary>
public sealed record EmailMessage
{
    public required IReadOnlyList<EmailAddress> To { get; init; }
    public required string Subject { get; init; }

    /// <summary>Provide <see cref="HtmlBody"/> and/or <see cref="TextBody"/> — a well-formed email
    /// carries both (multipart/alternative) so non-HTML clients still render something.</summary>
    public string? HtmlBody { get; init; }
    public string? TextBody { get; init; }

    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];
    public EmailAddress? ReplyTo { get; init; }

    /// <summary>Overrides the configured default sender (<see cref="EmailOptions.From"/>);
    /// usually left null.</summary>
    public EmailAddress? From { get; init; }

    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}
