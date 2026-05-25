using Api.Data;
using Api.Http;
using Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Posts;

public static class PostsEndpoints
{
    public static IEndpointRouteBuilder MapPostsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/posts").WithTags("Posts");

        group.MapGet("/", GetAll);

        group.MapGet("/{id:guid}", GetById)
            // 304 isn't carried by StatusCodeHttpResult's static metadata; advertise it explicitly.
            .Produces(StatusCodes.Status304NotModified)
            .WithConditionalRead();

        group.MapPost("/", Create)
            .AddEndpointFilter<ValidationEndpointFilter<CreatePostRequest>>()
            // The validation filter — not the handler — produces the 400. Advertise it here.
            .ProducesValidationProblem()
            .WithEtagResponseHeader();

        group.MapPut("/{id:guid}", Update)
            .AddEndpointFilter<ValidationEndpointFilter<UpdatePostRequest>>()
            .ProducesValidationProblem()
            // ProblemHttpResult only declares 500 statically; declare the codes we actually return.
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithConditionalWrite();

        group.MapDelete("/{id:guid}", Delete)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .WithConditionalWrite();

        return app;
    }

    private static async Task<Ok<IEnumerable<PostResponse>>> GetAll(
        AppDbContext db, PostMapper mapper, CancellationToken ct)
    {
        var posts = await db.Posts
            .AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return TypedResults.Ok(posts.Select(mapper.ToResponse));
    }

    private static async Task<Results<Ok<PostResponse>, NotFound, StatusCodeHttpResult>> GetById(
        Guid id, HttpContext http, AppDbContext db, PostMapper mapper, CancellationToken ct)
    {
        // Tracked load (no AsNoTracking) so ETag.From(db, post) can read the xmin
        // shadow property via the change tracker.
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);

        if (post is null)
        {
            return TypedResults.NotFound();
        }

        var etag = ETag.From(db, post);
        if (ConditionalRequest.EvaluateRead(http, etag) is { } notModified)
        {
            return notModified;
        }

        return TypedResults.Ok(mapper.ToResponse(post));
    }

    private static async Task<Created<PostResponse>> Create(
        CreatePostRequest request, HttpContext http, AppDbContext db, PostMapper mapper, CancellationToken ct)
    {
        var post = mapper.ToEntity(request);
        db.Posts.Add(post);
        await db.SaveChangesAsync(ct);

        ConditionalRequest.SetETagHeader(http, ETag.From(db, post));
        // The "/v1" prefix is owned by Program.cs's MapGroup("/v1"). Mirrored here in the
        // Location header — keep this hardcoded for now; revisit (LinkGenerator, route names,
        // or a per-feature RoutePrefix const) when a second versioned resource shows up.
        return TypedResults.Created($"/v1/posts/{post.Id}", mapper.ToResponse(post));
    }

    private static async Task<Results<Ok<PostResponse>, NotFound, ProblemHttpResult>> Update(
        Guid id, UpdatePostRequest request, HttpContext http, AppDbContext db, PostMapper mapper, CancellationToken ct)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
        {
            return TypedResults.NotFound();
        }

        if (ConditionalRequest.EvaluateWrite(http, ETag.From(db, post)) is { } preconditionFailure)
        {
            return preconditionFailure;
        }

        mapper.Apply(request, post);
        if (await ConcurrentSave.TryAsync<Post>(db, http, id, ct) is { } concurrencyFailure)
        {
            return concurrencyFailure;
        }

        ConditionalRequest.SetETagHeader(http, ETag.From(db, post));
        return TypedResults.Ok(mapper.ToResponse(post));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> Delete(
        Guid id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
        {
            return TypedResults.NotFound();
        }

        if (ConditionalRequest.EvaluateWrite(http, ETag.From(db, post)) is { } preconditionFailure)
        {
            return preconditionFailure;
        }

        db.Posts.Remove(post);
        if (await ConcurrentSave.TryAsync<Post>(db, http, id, ct) is { } concurrencyFailure)
        {
            return concurrencyFailure;
        }

        return TypedResults.NoContent();
    }
}
