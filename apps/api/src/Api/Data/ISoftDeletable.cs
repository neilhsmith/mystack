namespace Api.Data;

/// <summary>
/// Marker interface for entities that support soft delete. The <see cref="AuditInterceptor"/>
/// intercepts EF's <c>Deleted</c> state on save and converts it to an UPDATE that sets
/// <c>DeletedAt</c>; the row is never actually removed by EF. A global query filter applied
/// in <c>AppDbContext.OnModelCreating</c> hides soft-deleted rows from every query by
/// default — use <c>IgnoreQueryFilters()</c> to include them (admin views, audit, etc.).
/// <para>
/// <b>HTTP semantics:</b> soft-deleted resources return <c>404 Not Found</c> on every verb,
/// indistinguishable from never-existed. Rationale:
/// </para>
/// <list type="bullet">
///   <item>Soft delete is an implementation detail — the public API contract is "the
///   resource is gone." Whether it's reversible by admin isn't part of the contract.</item>
///   <item><c>410 Gone</c> implies <i>permanent</i> removal (RFC 9110 §15.5.11). Soft delete
///   is precisely not permanent — admins can restore — so 410 would be a category error.</item>
///   <item>Returning 410 leaks existence information (an unauthenticated probe could
///   distinguish "this slug was once taken" from "never used"). 404 keeps it ambiguous.</item>
///   <item>404 needs no extra query to compute. 410 would require <c>IgnoreQueryFilters()</c>
///   on every "not found" path just to tell the two cases apart.</item>
/// </list>
/// </summary>
/// <remarks>
/// Hard delete is still possible via raw SQL when truly needed (GDPR erasure, maintenance).
/// </remarks>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}
