using Api.Data;
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
        group.MapGet("/{id:guid}", GetById);

        group.MapPost("/", Create)
            .AddEndpointFilter<ValidationEndpointFilter<CreatePostRequest>>()
            .ProducesValidationProblem();

        group.MapPut("/{id:guid}", Update)
            .AddEndpointFilter<ValidationEndpointFilter<UpdatePostRequest>>()
            .ProducesValidationProblem();

        group.MapDelete("/{id:guid}", Delete);

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

    private static async Task<Results<Ok<PostResponse>, NotFound>> GetById(
        Guid id, AppDbContext db, PostMapper mapper, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return post is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(mapper.ToResponse(post));
    }

    private static async Task<Created<PostResponse>> Create(
        CreatePostRequest request, AppDbContext db, PostMapper mapper, CancellationToken ct)
    {
        var post = mapper.ToEntity(request);
        db.Posts.Add(post);
        await db.SaveChangesAsync(ct);

        // The "/v1" prefix is owned by Program.cs's MapGroup("/v1"). Mirrored here in the
        // Location header — keep this hardcoded for now; revisit (LinkGenerator, route names,
        // or a per-feature RoutePrefix const) when a second versioned resource shows up.
        return TypedResults.Created($"/v1/posts/{post.Id}", mapper.ToResponse(post));
    }

    private static async Task<Results<Ok<PostResponse>, NotFound>> Update(
        Guid id, UpdatePostRequest request, AppDbContext db, PostMapper mapper, CancellationToken ct)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
        {
            return TypedResults.NotFound();
        }

        mapper.Apply(request, post);
        // A concurrent writer that bumped xmin between the load above and the save below
        // surfaces as DbUpdateConcurrencyException → 409, mapped by
        // DbUpdateConcurrencyExceptionHandler. Handler stays oblivious.
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(mapper.ToResponse(post));
    }

    private static async Task<Results<NoContent, NotFound>> Delete(
        Guid id, AppDbContext db, CancellationToken ct)
    {
        var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (post is null)
        {
            return TypedResults.NotFound();
        }

        db.Posts.Remove(post);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
