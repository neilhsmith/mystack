namespace Api.Data;

/// <summary>
/// Marker interface for entities that support soft delete. The <see cref="AuditInterceptor"/>
/// intercepts EF's <c>Deleted</c> state on save and converts it to an UPDATE that sets
/// <c>DeletedAt</c>; the row is never actually removed by EF. A global query filter applied
/// in <c>AppDbContext.OnModelCreating</c> hides soft-deleted rows from every query by
/// default — use <c>IgnoreQueryFilters()</c> to include them (admin views, audit, etc.).
/// </summary>
/// <remarks>
/// Hard delete is still possible via raw SQL when truly needed (GDPR erasure, maintenance).
/// </remarks>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}
