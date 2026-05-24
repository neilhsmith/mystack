using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Api.Http;

/// <summary>
/// Builds strong RFC 7232 entity tags from an entity's Postgres <c>xmin</c> rowversion.
/// <para>
/// The tag is the entity's <c>xmin</c> value (a 32-bit unsigned int the database bumps
/// on every UPDATE) formatted as 8 hex characters wrapped in quotes — e.g. <c>"00012A8F"</c>.
/// EF tracks <c>xmin</c> as a shadow property on every entity (see <c>AppDbContext.OnModelCreating</c>'s
/// convention loop). The same value backs EF's concurrency check, so the HTTP precondition
/// (<c>If-Match</c>) and the DB-level optimistic-concurrency check always agree on what
/// "current" means — no risk of one passing and the other failing for the same row state.
/// </para>
/// </summary>
public static class ETag
{
    /// <summary>
    /// Build an ETag from a tracked entity. Reads the <c>xmin</c> shadow property the
    /// Npgsql convention stamps onto every entity. Pass an entity that's currently tracked
    /// by <paramref name="db"/> (any state — Added, Modified, Unchanged) so EF can resolve
    /// the value.
    /// </summary>
    public static EntityTagHeaderValue From(DbContext db, object entity) =>
        From((uint)db.Entry(entity).Property("xmin").CurrentValue!);

    /// <summary>
    /// Build an ETag from a raw <c>xmin</c> value. Useful for tests and for callers that
    /// already have the value in hand.
    /// </summary>
    public static EntityTagHeaderValue From(uint xmin)
    {
        var value = "\"" + xmin.ToString("X8") + "\"";
        return new EntityTagHeaderValue(value, isWeak: false);
    }
}
