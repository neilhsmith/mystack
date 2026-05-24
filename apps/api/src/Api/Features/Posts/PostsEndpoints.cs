using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Posts;

public static class PostsEndpoints
{
    public static IEndpointRouteBuilder MapPostsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/posts").WithTags("Posts");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var posts = await db.Posts
                .AsNoTracking()
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(posts.Select(p => p.ToResponse()));
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, CancellationToken ct) =>
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

            return Results.Ok(post.ToResponse());
        });

        group.MapPost("/", async (CreatePostRequest request, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (ValidateBody(request.Title, request.Content) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var post = new Post { Title = request.Title, Content = request.Content };
            db.Posts.Add(post);
            await db.SaveChangesAsync(ct);

            ConditionalRequest.SetETagHeader(http, ETag.From(post));
            return Results.Created($"/posts/{post.Id}", post.ToResponse());
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdatePostRequest request, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (ValidateBody(request.Title, request.Content) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (post is null)
            {
                return Results.NotFound();
            }

            if (ConditionalRequest.EvaluateWrite(http, ETag.From(post)) is { } preconditionFailure)
            {
                return preconditionFailure;
            }

            post.Title = request.Title;
            post.Content = request.Content;
            await db.SaveChangesAsync(ct);

            ConditionalRequest.SetETagHeader(http, ETag.From(post));
            return Results.Ok(post.ToResponse());
        });

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

    private static string? ValidateBody(string title, string content)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Title is required.";
        }

        if (title.Length > 200)
        {
            return "Title must be 200 characters or fewer.";
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return "Content is required.";
        }

        return null;
    }
}
