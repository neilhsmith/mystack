using Api.Data;
using Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Posts;

public static class PostsEndpoints
{
    public static IEndpointRouteBuilder MapPostsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/posts").WithTags("Posts");

        group.MapGet("/", async (AppDbContext db, PostMapper mapper, CancellationToken ct) =>
        {
            var posts = await db.Posts
                .AsNoTracking()
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(posts.Select(mapper.ToResponse));
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, PostMapper mapper, CancellationToken ct) =>
        {
            var post = await db.Posts
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id, ct);

            if (post is null)
            {
                return Results.NotFound();
            }

            var etag = ETag.From(post);
            if (ConditionalRequest.EvaluateRead(http, etag) is { } notModified)
            {
                return notModified;
            }

            return Results.Ok(mapper.ToResponse(post));
        });

        group.MapPost("/", async (CreatePostRequest request, HttpContext http, AppDbContext db, PostMapper mapper, CancellationToken ct) =>
            {
                var post = mapper.ToEntity(request);
                db.Posts.Add(post);
                await db.SaveChangesAsync(ct);

                ConditionalRequest.SetETagHeader(http, ETag.From(post));
                return Results.Created($"/posts/{post.Id}", mapper.ToResponse(post));
            })
            .AddEndpointFilter<ValidationEndpointFilter<CreatePostRequest>>();

        group.MapPut("/{id:guid}", async (Guid id, UpdatePostRequest request, HttpContext http, AppDbContext db, PostMapper mapper, CancellationToken ct) =>
            {
                var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
                if (post is null)
                {
                    return Results.NotFound();
                }

                if (ConditionalRequest.EvaluateWrite(http, ETag.From(post)) is { } preconditionFailure)
                {
                    return preconditionFailure;
                }

                mapper.Apply(request, post);
                await db.SaveChangesAsync(ct);

                ConditionalRequest.SetETagHeader(http, ETag.From(post));
                return Results.Ok(mapper.ToResponse(post));
            })
            .AddEndpointFilter<ValidationEndpointFilter<UpdatePostRequest>>();

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (post is null)
            {
                return Results.NotFound();
            }

            if (ConditionalRequest.EvaluateWrite(http, ETag.From(post)) is { } preconditionFailure)
            {
                return preconditionFailure;
            }

            db.Posts.Remove(post);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        return app;
    }
}
