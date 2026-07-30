using System.Security.Claims;
using System.Text.Json;

namespace MyStack.Observability;

internal static class ActorClaim
{
    /// <summary>
    /// The acting party's subject from an RFC 8693 <c>act</c> claim, or null when the principal
    /// isn't impersonated.
    /// </summary>
    public static string? Sub(ClaimsPrincipal principal)
    {
        var value = principal.FindFirst("act")?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // RFC 8693 §4.1: `act` is a JSON object whose `sub` names the current actor; a nested
        // `act` member records earlier hops in a delegation chain, so only the top level is read.
        // Anything malformed enriches nothing — this is telemetry, and telemetry never throws.
        try
        {
            using var act = JsonDocument.Parse(value);

            if (
                act.RootElement.ValueKind == JsonValueKind.Object
                && act.RootElement.TryGetProperty("sub", out var sub)
                && sub.ValueKind == JsonValueKind.String
            )
            {
                return string.IsNullOrWhiteSpace(sub.GetString()) ? null : sub.GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through: an `act` value that isn't the RFC shape is treated as absent.
        }

        return null;
    }
}
