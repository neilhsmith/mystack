using Api.Data;

namespace Api.Features.Posts;

public sealed class Post : IHasTimestamps
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Title { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
