namespace Api.Features.Posts;

public sealed record CreatePostRequest(string Title, string Content);

public sealed record UpdatePostRequest(string Title, string Content);

public sealed record PostResponse(
    Guid Id,
    string Title,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal static class PostMappings
{
    public static PostResponse ToResponse(this Post post) =>
        new(post.Id, post.Title, post.Content, post.CreatedAt, post.UpdatedAt);
}
