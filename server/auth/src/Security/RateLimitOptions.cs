namespace MyStack.Auth.Security;

// Per-endpoint permits over one shared fixed window, partitioned by client IP. The split is
// deliberate: the email-driving endpoints cost a third party an email per request, so they get
// the tighter budget; the credential-verifying ones only cost this host CPU.
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public int WindowSeconds { get; set; } = 60;

    public int SignIn { get; set; } = 10;

    public int Register { get; set; } = 5;

    public int ForgotPassword { get; set; } = 5;

    public int ResendConfirmation { get; set; } = 5;

    public int ChangePassword { get; set; } = 10;

    public int Verify { get; set; } = 10;
}
