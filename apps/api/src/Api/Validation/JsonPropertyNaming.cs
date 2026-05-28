using System.Text.Json;

namespace Api.Validation;

/// <summary>
/// CLR-property-name → JSON-property-name mapping. Used wherever a CLR identifier
/// (a FluentValidation property path, an EF property name, etc.) needs to be quoted
/// against the JSON contract the API actually serializes.
/// <para>
/// Backed by <see cref="JsonNamingPolicy.CamelCase"/> so corner cases like all-caps
/// prefixes (<c>URL</c> → <c>url</c>) and acronyms (<c>HttpStatusCode</c> →
/// <c>httpStatusCode</c>) follow the same rules the JSON serializer applies to DTO
/// property names. That keeps validation error keys, OpenAPI schema property names,
/// and the on-the-wire JSON aligned with no per-call-site special-casing.
/// </para>
/// <para>
/// If a DTO ever uses <c>[JsonPropertyName]</c> to override the default policy, this
/// helper will be wrong for that property — revisit and resolve via the request's
/// <c>JsonTypeInfo</c> at that point.
/// </para>
/// </summary>
public static class JsonPropertyNaming
{
    /// <summary>
    /// Convert a single CLR identifier (<c>Title</c>) to its JSON form (<c>title</c>).
    /// </summary>
    public static string ToJsonName(string clrName) =>
        string.IsNullOrEmpty(clrName)
            ? clrName
            : JsonNamingPolicy.CamelCase.ConvertName(clrName);

    /// <summary>
    /// Convert a FluentValidation-shaped property path (<c>Address.Street</c>,
    /// <c>Tags[0].Name</c>) to the matching JSON path (<c>address.street</c>,
    /// <c>tags[0].name</c>). Each dot-separated segment is converted independently so
    /// every level of nesting follows the same naming policy the serializer uses for
    /// the corresponding object.
    /// </summary>
    public static string ToJsonPath(string clrPath)
    {
        if (string.IsNullOrEmpty(clrPath))
        {
            return clrPath;
        }

        if (!clrPath.Contains('.'))
        {
            return ToJsonName(clrPath);
        }

        return string.Join('.', clrPath.Split('.').Select(ToJsonName));
    }
}
