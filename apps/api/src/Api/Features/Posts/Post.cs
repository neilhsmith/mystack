using Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Features.Posts;

public sealed class Post : ITimestamped, ISoftDeletable
{
    public static class Constraints
    {
        public const int MaxTitleLength = 200;
        public const int MaxContentLength = 10_000;
    }

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Title { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>
/// Per-feature EF configuration for <see cref="Post"/>. Discovered automatically by
/// <c>AppDbContext.OnModelCreating</c> via <c>ApplyConfigurationsFromAssembly</c> so the
/// data layer doesn't need to know about individual features. Cross-cutting concerns
/// (auto timestamps, soft-delete query filter) live in <c>AppDbContext</c> and apply by
/// interface convention — don't repeat them here.
/// </summary>
internal sealed class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Title).IsRequired().HasMaxLength(Post.Constraints.MaxTitleLength);
        builder.Property(p => p.Content).IsRequired().HasMaxLength(Post.Constraints.MaxContentLength);
    }
}
