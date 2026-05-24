using Api.Data;
using Microsoft.Net.Http.Headers;

namespace Api.Http;

/// <summary>
/// Builds strong RFC 7232 entity tags from <see cref="ITimestamped.UpdatedAt"/>.
/// <para>
/// The tag is the entity's UTC ticks formatted as 16 hex characters wrapped in quotes
/// (e.g. <c>"08DDDB7D2A4E3000"</c>). <see cref="AuditInterceptor"/> truncates UpdatedAt
/// to microsecond precision on save so the in-memory value matches what Postgres
/// persists — meaning a freshly created entity and a re-read of the same row produce
/// the identical ETag. That stability is what makes <c>If-Match</c> / <c>If-None-Match</c>
/// usable.
/// </para>
/// </summary>
public static class ETag
{
    public static EntityTagHeaderValue From(ITimestamped entity) => From(entity.UpdatedAt);

    public static EntityTagHeaderValue From(DateTimeOffset updatedAt)
    {
        var value = "\"" + updatedAt.UtcTicks.ToString("X16") + "\"";
        return new EntityTagHeaderValue(value, isWeak: false);
    }
}
