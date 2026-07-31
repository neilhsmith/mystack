namespace MyStack.Email;

/// <summary>
/// SMTP settings, bound from the <c>Email</c> section. One adapter covers every environment
/// (architecture §3.3): locally these point at compose's Mailpit, elsewhere at whichever
/// provider's SMTP endpoint — only host, port and credentials change.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; init; } = "";

    /// <summary>587 is the submission default real providers use; Mailpit listens on 1025.</summary>
    public int Port { get; init; } = 587;

    /// <summary>The default sender address, used when a message doesn't set its own — a real
    /// provider usually requires a verified from-address.</summary>
    public string From { get; init; } = "";

    public string? FromName { get; init; }

    /// <summary>Credentials are optional because Mailpit needs none; when <see cref="Username"/>
    /// is set the sender authenticates with it.</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }
}
