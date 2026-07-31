namespace MyStack.Auth.Account;

public sealed class AccountOptions
{
    public const string SectionName = "Account";

    // The public origin emailed links are built from. Never derived from the request: the Host
    // header is client-writable, and a forged one on forgot-password would steer a victim's real
    // reset link to an attacker's domain.
    public string PublicBaseUrl { get; init; } = "";
}
