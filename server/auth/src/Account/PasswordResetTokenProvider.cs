using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MyStack.Auth.Account;

// A reset token is a full account-takeover credential — it sets a new password outright — while a
// confirmation token only flips a boolean. So reset gets its own provider with a short lifespan,
// and confirmation stays on Identity's default provider and its 24 hours.
internal sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public const string ProviderName = "PasswordReset";

    public PasswordResetTokenProviderOptions()
    {
        Name = ProviderName;
        TokenLifespan = TimeSpan.FromHours(2);
    }
}

internal sealed class PasswordResetTokenProvider<TUser>(
    IDataProtectionProvider dataProtection,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<TUser>> logger
) : DataProtectorTokenProvider<TUser>(dataProtection, options, logger)
    where TUser : class;
