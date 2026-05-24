using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Api.Data;

/// <summary>
/// Centralised on-save hooks for managed entity concerns:
/// <list type="bullet">
///   <item><see cref="ISoftDeletable"/>: intercepts <c>Deleted</c> state and converts it
///   to an UPDATE that sets <c>DeletedAt = now()</c>. The row is never hard-deleted by EF.</item>
///   <item><see cref="ITimestamped"/>: stamps <c>CreatedAt</c>/<c>UpdatedAt</c> — both on insert,
///   <c>UpdatedAt</c> only on update, <c>CreatedAt</c> defended against accidental mutation.
///   Values are truncated to microsecond precision so the in-memory entity matches what
///   Postgres <c>timestamptz</c> stores (lets ETags / conditional GETs work without drift).</item>
/// </list>
/// Soft-delete pass runs first so converted-to-Modified entries flow through the timestamp
/// pass naturally and pick up an UpdatedAt bump.
/// </summary>
public sealed class AuditInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = TruncateToMicroseconds(timeProvider.GetUtcNow());

        // 1. Soft delete: rewrite Deleted entries to Modified with DeletedAt set.
        foreach (var entry in context.ChangeTracker.Entries<ISoftDeletable>())
        {
            if (entry.State == EntityState.Deleted)
            {
                entry.Entity.DeletedAt = now;
                entry.State = EntityState.Modified;
            }
        }

        // 2. Timestamps: applies to entries the soft-delete pass just rewrote, too.
        foreach (var entry in context.ChangeTracker.Entries<ITimestamped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    // Defensive: never let an update mutate CreatedAt.
                    entry.Property(nameof(ITimestamped.CreatedAt)).IsModified = false;
                    break;
            }
        }
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset dt) =>
        new(dt.Ticks - (dt.Ticks % 10), dt.Offset);
}
