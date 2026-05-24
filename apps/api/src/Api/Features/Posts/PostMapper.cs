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

    // The [MapperIgnoreTarget] attributes on ToEntity / Apply are LOAD-BEARING, not
    // warning suppressors. With RequiredMappingStrategy.Target, Mapperly emits an
    // assignment for every settable property on the target unless explicitly ignored.
    // Without these ignores, ToEntity would zero out Id (overwriting Guid.CreateVersion7())
    // and Apply would zero out CreatedAt/UpdatedAt/DeletedAt on every PUT, breaking
    // timestamps, soft-delete, and ETags. Add a new ignore for every framework-managed
    // field you add to Post — `xmin` is a shadow property so it doesn't need one.

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
