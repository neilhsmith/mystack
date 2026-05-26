using System.Text.Json;
using OpenIddict.Abstractions;

namespace Api.Features.Auth;

/// <summary>
/// Reads + writes the role list attached to an OpenIddict application via the
/// <c>Properties</c> JSON bag. Service clients (client_credentials) get role claims from
/// here instead of from an Identity user — so the resource server treats them with the
/// same role→permission pipeline as authenticated users.
/// <para>
/// The roles are stored under <see cref="PropertyKey"/> as a JSON array of strings on the
/// application's <see cref="OpenIddictApplicationDescriptor.Properties"/> dictionary
/// (<c>application_properties</c> column in the DB).
/// </para>
/// </summary>
public static class ClientRoles
{
    /// <summary>Key used for the roles array in <c>application_properties</c>.</summary>
    public const string PropertyKey = "mystack:roles";

    public static void Set(OpenIddictApplicationDescriptor descriptor, params string[] roles)
    {
        descriptor.Properties[PropertyKey] = JsonSerializer.SerializeToElement(roles);
    }

    /// <summary>
    /// Read the role list from a loaded application (via <see cref="IOpenIddictApplicationManager"/>).
    /// Returns an empty array if the property is absent or malformed.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetAsync(
        IOpenIddictApplicationManager applications,
        object application,
        CancellationToken ct)
    {
        var properties = await applications.GetPropertiesAsync(application, ct);
        if (properties.TryGetValue(PropertyKey, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }
        return [];
    }
}
