using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace MyStack.Auth.Account;

// Identity's data-protector tokens are standard Base64 — `+`, `/`, `=` — and query strings mangle
// exactly those: `+` decodes to a space under form/query decoding, and mail-client link rewriters
// chew on the rest. Base64Url over the UTF-8 bytes survives the trip. This pair is the only place
// the transform lives, so the emailing end and the consuming end can never drift.
internal static class AccountTokens
{
    public static string Encode(string token) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    // A tampered link must read as an invalid token, never throw a FormatException out of
    // Base64UrlDecode into a 500 — a distinct failure shape is an enumeration signal.
    public static bool TryDecode(string? encoded, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
