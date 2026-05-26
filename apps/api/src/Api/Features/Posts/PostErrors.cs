using ErrorOr;

namespace Api.Features.Posts;

/// <summary>
/// Centralised <see cref="Error"/> factories for the Posts feature. One place to look
/// for "what failure modes does Posts surface?" — the convention is that every error a
/// <see cref="PostsService"/> method can return is declared here, not constructed inline.
/// <para>
/// The <see cref="Error.Code"/> values are stable, machine-readable strings (<c>"posts.not_found"</c>
/// etc.) — they're part of the API contract a client may key off. The <see cref="Error.Description"/>
/// values are user-safe (no internal detail, no stack trace, no SQL) because they end up
/// in the problem+json response body via <see cref="Api.Http.ErrorResults"/>.
/// </para>
/// </summary>
public static class PostErrors
{
    /// <summary>
    /// The requested post does not exist (or has been soft-deleted, which is indistinguishable
    /// from "never existed" at the HTTP boundary — see <see cref="Api.Data.ISoftDeletable"/>).
    /// Maps to <c>404 Not Found</c> via <see cref="Api.Http.ErrorResults.StatusFor(ErrorType)"/>.
    /// </summary>
    public static Error NotFound(Guid id) =>
        Error.NotFound(code: "posts.not_found", description: $"Post {id} was not found.");
}
