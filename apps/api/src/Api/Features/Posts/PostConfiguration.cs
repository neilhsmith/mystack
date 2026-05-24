using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Features.Posts;

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
