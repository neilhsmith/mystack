namespace Api.Data;

/// <summary>
/// Marker interface for entities whose <c>CreatedAt</c> / <c>UpdatedAt</c> should be
/// managed automatically by <see cref="TimestampsInterceptor"/> on SaveChanges.
/// Entities must NOT set these properties manually — the interceptor is the single
/// source of truth so API responses match persisted state (ETags rely on this).
/// </summary>
public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
