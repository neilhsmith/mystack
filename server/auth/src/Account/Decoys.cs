using Microsoft.AspNetCore.Identity;
using MyStack.Auth.Data;

namespace MyStack.Auth.Account;

// Anti-enumeration's timing half. The pages already give one answer for hit and miss; these make
// the miss paths *cost* what the hit paths cost, so response time doesn't whisper what the page
// won't say. Database and broker writes on the hit paths remain a small residual — the rate
// limiter is the brake on measuring it.
internal static class Decoys
{
    // A user-shaped object that exists only to be hashed and tokenized against; never stored.
    private static readonly ApplicationUser User = new()
    {
        Id = Guid.NewGuid(),
        UserName = "decoy@mystack.invalid",
        Email = "decoy@mystack.invalid",
        SecurityStamp = Guid.NewGuid().ToString("N"),
    };

    private static string? passwordHash;

    public static void VerifyPassword(IPasswordHasher<ApplicationUser> hasher, string password)
    {
        // Hashed with the live hasher so the decoy verification always costs what a real one
        // does, whatever the configured work factor. The race is benign: two first callers hash
        // twice, everyone after shares the result.
        passwordHash ??= hasher.HashPassword(User, Guid.NewGuid().ToString("N"));
        _ = hasher.VerifyHashedPassword(User, passwordHash, password);
    }

    public static void HashPassword(IPasswordHasher<ApplicationUser> hasher, string password) =>
        _ = hasher.HashPassword(User, password);

    public static Task PasswordResetTokenAsync(UserManager<ApplicationUser> users) =>
        users.GeneratePasswordResetTokenAsync(User);

    public static Task EmailConfirmationTokenAsync(UserManager<ApplicationUser> users) =>
        users.GenerateEmailConfirmationTokenAsync(User);
}
