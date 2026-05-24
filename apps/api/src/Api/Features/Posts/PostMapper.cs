using Riok.Mapperly.Abstractions;

namespace Api.Features.Posts;

/// <summary>
/// Mapperly-generated mappings between <see cref="Post"/> and its request/response DTOs.
/// <para>
/// Convention for this repo: one <c>[Mapper] partial class &lt;Feature&gt;Mapper</c> per
/// feature folder, registered as a singleton in <c>Program.cs</c>. Source-generated, so
/// the mapping code is plain C# you can step through — no runtime reflection cost.
/// </para>
/// <para>
/// <see cref="RequiredMappingStrategy.Target"/> means Mapperly only requires every target
/// property be mapped, and tolerates extra source properties (e.g. <see cref="Post.DeletedAt"/>
/// when projecting to <see cref="PostResponse"/>). Target ignores below silence the inverse
/// case: writing to the entity, we leave framework-managed fields alone.
/// </para>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class PostMapper
{
    public partial PostResponse ToResponse(Post source);

    [MapperIgnoreTarget(nameof(Post.Id))]
    [MapperIgnoreTarget(nameof(Post.CreatedAt))]
    [MapperIgnoreTarget(nameof(Post.UpdatedAt))]
    [MapperIgnoreTarget(nameof(Post.DeletedAt))]
    public partial Post ToEntity(CreatePostRequest source);

    [MapperIgnoreTarget(nameof(Post.Id))]
    [MapperIgnoreTarget(nameof(Post.CreatedAt))]
    [MapperIgnoreTarget(nameof(Post.UpdatedAt))]
    [MapperIgnoreTarget(nameof(Post.DeletedAt))]
    public partial void Apply(UpdatePostRequest source, Post target);
}
