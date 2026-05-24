namespace Api.Features.Posts;

public sealed class Post
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Title { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = MicrosecondUtcNow();

    public DateTimeOffset UpdatedAt { get; set; } = MicrosecondUtcNow();

    // Postgres timestamptz is microsecond-precise; .NET DateTimeOffset is 100ns-precise.
    // Truncate at source so in-memory values match what gets persisted (otherwise the POST
    // response can claim sub-microsecond precision that the next GET won't reproduce).
    internal static DateTimeOffset MicrosecondUtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % 10), now.Offset);
    }
}
