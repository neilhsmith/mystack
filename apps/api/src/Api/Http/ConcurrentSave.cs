using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Api.Http;

/// <summary>
/// Wraps <see cref="DbContext.SaveChangesAsync(System.Threading.CancellationToken)"/> for
/// write endpoints that surface ETags. Catches the race where another writer bumps the
/// row's <c>xmin</c> between this request's load and save, refreshes the response ETag
/// header from the now-current row, and returns a 412 Precondition Failed result.
/// <para>
/// The <c>If-Match</c> check earlier in the handler guards against the client supplying
/// a stale tag; this guards against a stale tag being introduced server-side by a
/// concurrent writer. Both surface the same way (412 + current ETag) so the client can
/// refetch and retry uniformly.
/// </para>
/// </summary>
public static class ConcurrentSave
{
    /// <summary>
    /// Save tracked changes; on <see cref="DbUpdateConcurrencyException"/>, refetch the
    /// entity by primary key, refresh the response ETag header, and return a 412 result.
    /// Returns <c>null</c> on success so callers can use it inline:
    /// <c>if (await ConcurrentSave.TryAsync&lt;Foo&gt;(db, http, id, ct) is { } failure) return failure;</c>
    /// <para>
    /// If the row was hard-deleted or soft-deleted concurrently, the refetch returns null
    /// (the global query filter hides soft-deleted rows) and the ETag header is omitted.
    /// </para>
    /// </summary>
    public static async Task<ProblemHttpResult?> TryAsync<TEntity>(
        DbContext db, HttpContext http, Guid id, CancellationToken ct)
        where TEntity : class
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Detach the stale entry and refetch — needs tracking (FindAsync returns
            // tracked) so ETag.From(db, current) can read the xmin shadow property off
            // the change tracker. FindAsync routes through the query pipeline when the
            // tracker is empty, so the global soft-delete query filter still applies.
            db.ChangeTracker.Clear();
            var current = await db.Set<TEntity>().FindAsync([id], ct);
            if (current is not null)
            {
                ConditionalRequest.SetETagHeader(http, ETag.From(db, current));
            }
            return TypedResults.Problem(
                statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Resource was modified by another writer.");
        }
    }
}
