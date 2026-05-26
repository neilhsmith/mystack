using Api.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Posts;

/// <summary>
/// Business operations for the Posts resource. Owns the DbContext interaction so endpoint
/// handlers stay thin — they translate HTTP to service calls and the resulting
/// <see cref="ErrorOr{TValue}"/> to <c>application/problem+json</c> via
/// <see cref="Api.Http.ErrorResults"/>.
/// <para>
/// <strong>Return-type conventions:</strong>
/// </para>
/// <list type="bullet">
///   <item><see cref="GetAllAsync"/> returns the list directly — empty is a valid success,
///   there is no failure mode worth modelling.</item>
///   <item><see cref="GetByIdAsync"/>, <see cref="UpdateAsync"/>, <see cref="DeleteAsync"/>
///   return <see cref="ErrorOr{TValue}"/> wrapping <see cref="Post"/> or
///   <see cref="Deleted"/>; the only failure today is <see cref="PostErrors.NotFound(Guid)"/>,
///   but the signature is future-proof for business rules (conflict, forbidden, …).</item>
///   <item><see cref="CreateAsync"/> returns <see cref="ErrorOr{TValue}"/> even though
///   creation can't fail today — the signature reserves room for "duplicate title" or
///   similar without re-typing every caller later.</item>
/// </list>
/// <para>
/// <strong>What this service does NOT do:</strong>
/// </para>
/// <list type="bullet">
///   <item>Catch <see cref="DbUpdateConcurrencyException"/>. EF surfaces lost-update races
///   from the <c>xmin</c> token in <c>UPDATE WHERE xmin = &lt;stale&gt;</c>; the global
///   <see cref="Api.Http.DbUpdateConcurrencyExceptionHandler"/> maps that to <c>409</c>.
///   Endpoints and services stay oblivious.</item>
///   <item>Compute ETags. <see cref="Api.Http.EtagMiddleware"/> hashes the response body
///   for every GET and emits the header — no per-handler plumbing.</item>
///   <item>Validate the request shape. <see cref="Api.Validation.ValidationEndpointFilter{T}"/>
///   runs FluentValidation before the handler ever calls the service.</item>
/// </list>
/// </summary>
public sealed class PostsService
{
    private readonly AppDbContext _db;
    private readonly PostMapper _mapper;

    public PostsService(AppDbContext db, PostMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    /// <summary>
    /// All non-deleted posts, newest first. Empty list is a valid success — there is no
    /// "not found" for a collection, hence no <see cref="ErrorOr{TValue}"/> wrapping.
    /// </summary>
    public async Task<IReadOnlyList<Post>> GetAllAsync(CancellationToken ct)
    {
        return await _db.Posts
            .AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Look up a single post by id. Returns <see cref="PostErrors.NotFound(Guid)"/> if no
    /// row exists (or the row is soft-deleted — the global query filter hides those).
    /// </summary>
    public async Task<ErrorOr<Post>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var post = await _db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return post is null ? PostErrors.NotFound(id) : post;
    }

    /// <summary>
    /// Persist a new post. The DTO has already passed
    /// <see cref="Api.Validation.ValidationEndpointFilter{T}"/>, so request-shape failures
    /// don't reach this method.
    /// </summary>
    public async Task<ErrorOr<Post>> CreateAsync(CreatePostRequest request, CancellationToken ct)
    {
        var post = _mapper.ToEntity(request);
        _db.Posts.Add(post);
        await _db.SaveChangesAsync(ct);
        return post;
    }

    /// <summary>
    /// Apply <paramref name="request"/> to the post identified by <paramref name="id"/>.
    /// Returns <see cref="PostErrors.NotFound(Guid)"/> if the row is missing or soft-deleted.
    /// A concurrent writer that bumped <c>xmin</c> between the load and save surfaces as
    /// <see cref="DbUpdateConcurrencyException"/>, mapped to <c>409</c> by the global
    /// exception handler — no try/catch here.
    /// </summary>
    public async Task<ErrorOr<Post>> UpdateAsync(Guid id, UpdatePostRequest request, CancellationToken ct)
    {
        var post = await _db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
        {
            return PostErrors.NotFound(id);
        }

        _mapper.Apply(request, post);
        await _db.SaveChangesAsync(ct);
        return post;
    }

    /// <summary>
    /// Soft-delete the post identified by <paramref name="id"/>. Returns
    /// <see cref="PostErrors.NotFound(Guid)"/> if the row is missing or already soft-deleted.
    /// Returns <see cref="Result.Deleted"/> on success (ErrorOr's marker for "the operation
    /// succeeded but there's no value to return", a clearer signal than <see cref="Success"/>
    /// in the delete path).
    /// </summary>
    public async Task<ErrorOr<Deleted>> DeleteAsync(Guid id, CancellationToken ct)
    {
        var post = await _db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
        {
            return PostErrors.NotFound(id);
        }

        _db.Posts.Remove(post);
        await _db.SaveChangesAsync(ct);
        return Result.Deleted;
    }
}
