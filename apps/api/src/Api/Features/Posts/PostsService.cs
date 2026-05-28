using Api.Data;
using Api.Validation;
using ErrorOr;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Posts;

/// <summary>
/// Business operations for the Posts resource. Owns the DbContext interaction *and* the
/// request-shape validation, so the endpoint layer is a pure HTTP translator: it calls
/// one service method, gets back <see cref="ErrorOr{TValue}"/>, and routes the result
/// through <see cref="Api.Http.ErrorResults"/>.
/// <para>
/// <strong>Validation lives here, not at the endpoint.</strong> Request DTOs are validated
/// inside <see cref="CreateAsync"/> and <see cref="UpdateAsync"/> via the injected
/// <see cref="IValidator{T}"/>s. Failures are returned as <c>List&lt;Error.Validation&gt;</c>
/// with the JSON property path in <see cref="Error.Code"/> — <see cref="Api.Http.ErrorResults.ToProblem(List{Error})"/>
/// groups by code to produce the standard <c>application/problem+json</c> validation envelope.
/// This means anyone calling the service — HTTP handler, Hangfire job, integration test —
/// gets validation for free; there is no "trust the caller validated already" assumption.
/// </para>
/// <para>
/// <strong>Return-type conventions:</strong>
/// </para>
/// <list type="bullet">
///   <item><see cref="GetAllAsync"/> returns the list directly — empty is a valid success,
///   there is no failure mode worth modelling.</item>
///   <item><see cref="GetByIdAsync"/>, <see cref="UpdateAsync"/>, <see cref="DeleteAsync"/>
///   return <see cref="ErrorOr{TValue}"/> wrapping <see cref="Post"/> or
///   <see cref="Deleted"/>; failure modes today are <see cref="PostErrors.NotFound(Guid)"/>
///   plus (on the write paths) request-shape validation errors.</item>
///   <item><see cref="CreateAsync"/> returns <see cref="ErrorOr{TValue}"/> so the validation
///   failure mode has somewhere to land; creation has no other failure today, but the
///   signature is future-proof for "duplicate title" or similar.</item>
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
/// </list>
/// </summary>
public sealed class PostsService
{
    private readonly AppDbContext _db;
    private readonly PostMapper _mapper;
    private readonly IValidator<CreatePostRequest> _createValidator;
    private readonly IValidator<UpdatePostRequest> _updateValidator;

    public PostsService(
        AppDbContext db,
        PostMapper mapper,
        IValidator<CreatePostRequest> createValidator,
        IValidator<UpdatePostRequest> updateValidator)
    {
        _db = db;
        _mapper = mapper;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
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
    /// Persist a new post. The request is validated first; validation failures short-circuit
    /// the DB roundtrip and surface as 400 problem+json via <see cref="Api.Http.ErrorResults"/>.
    /// </summary>
    public async Task<ErrorOr<Post>> CreateAsync(CreatePostRequest request, CancellationToken ct)
    {
        var validation = await _createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ToValidationErrors(validation);
        }

        var post = _mapper.ToEntity(request);
        _db.Posts.Add(post);
        await _db.SaveChangesAsync(ct);
        return post;
    }

    /// <summary>
    /// Apply <paramref name="request"/> to the post identified by <paramref name="id"/>.
    /// Validation runs first so an invalid body returns 400 even when the id is missing —
    /// this preserves the "fail on shape before lookup" contract the old endpoint filter
    /// gave us. Returns <see cref="PostErrors.NotFound(Guid)"/> if the row is missing or
    /// soft-deleted. A concurrent writer that bumped <c>xmin</c> between the load and save
    /// surfaces as <see cref="DbUpdateConcurrencyException"/>, mapped to <c>409</c> by the
    /// global exception handler — no try/catch here.
    /// </summary>
    public async Task<ErrorOr<Post>> UpdateAsync(Guid id, UpdatePostRequest request, CancellationToken ct)
    {
        var validation = await _updateValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
        {
            return ToValidationErrors(validation);
        }

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

    /// <summary>
    /// Translate a FluentValidation result into <see cref="ErrorOr"/>'s validation-error list.
    /// <see cref="Error.Code"/> carries the JSON property path (e.g. <c>title</c>) so
    /// <see cref="Api.Http.ErrorResults.ToProblem(List{Error})"/> can group by code into the
    /// <c>errors</c> bag of the RFC 9457 validation envelope. Wire shape is identical to
    /// what the old <c>ValidationEndpointFilter</c> produced via <c>Results.ValidationProblem</c>.
    /// </summary>
    private static List<Error> ToValidationErrors(ValidationResult result) =>
        result.Errors
            .Select(e => Error.Validation(
                code: JsonPropertyNaming.ToJsonPath(e.PropertyName),
                description: e.ErrorMessage))
            .ToList();
}
