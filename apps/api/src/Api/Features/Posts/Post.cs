using Api.Data;

namespace Api.Features.Posts;

public sealed class Post : ITimestamped, ISoftDeletable
{
    // Shared scalar limits live on the entity so EF fluent config, FluentValidation,
    // and (via the schema transformer) OpenAPI all read from one source of truth.
    public const int MaxTitleLength = 200;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Title { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsDeleted => DeletedAt is not null;
}
