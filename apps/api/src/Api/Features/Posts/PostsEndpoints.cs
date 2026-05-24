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

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var post = await db.Posts
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id, ct);

            return post is null ? Results.NotFound() : Results.Ok(post.ToResponse());
        });

        group.MapPost("/", async (CreatePostRequest request, AppDbContext db, CancellationToken ct) =>
        {
            if (ValidateBody(request.Title, request.Content) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var post = new Post { Title = request.Title, Content = request.Content };
            db.Posts.Add(post);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/posts/{post.Id}", post.ToResponse());
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdatePostRequest request, AppDbContext db, CancellationToken ct) =>
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

            post.Title = request.Title;
            post.Content = request.Content;
            await db.SaveChangesAsync(ct);

            return Results.Ok(post.ToResponse());
        });

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var post = await db.Posts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (post is null)
            {
                return Results.NotFound();
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
