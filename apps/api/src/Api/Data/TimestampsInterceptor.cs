using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Api.Data;

/// <summary>
/// Stamps <c>CreatedAt</c> / <c>UpdatedAt</c> on entities implementing
/// <see cref="IHasTimestamps"/>: both set on insert, <c>UpdatedAt</c> only on update.
/// Values are truncated to microsecond precision so the in-memory entity matches what
/// Postgres <c>timestamptz</c> stores — API responses can be relied on for ETags /
/// conditional GETs without sub-microsecond drift between POST and the next GET.
/// </summary>
public sealed class TimestampsInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = TruncateToMicroseconds(timeProvider.GetUtcNow());

        foreach (var entry in context.ChangeTracker.Entries<IHasTimestamps>())
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
                    entry.Property(nameof(IHasTimestamps.CreatedAt)).IsModified = false;
                    break;
            }
        }
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset dt) =>
        new(dt.Ticks - (dt.Ticks % 10), dt.Offset);
}
