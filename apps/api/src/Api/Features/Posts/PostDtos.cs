using FluentValidation;

namespace Api.Features.Posts;

public sealed record CreatePostRequest(string Title, string Content);

public sealed record UpdatePostRequest(string Title, string Content);

public sealed record PostResponse(
    Guid Id,
    string Title,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class CreatePostRequestValidator : AbstractValidator<CreatePostRequest>
{
    public CreatePostRequestValidator()
    {
        // Stop at the first failure per property so callers see one message per field
        // instead of "Title is required" duplicated by NotEmpty + the whitespace guard.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            // NotEmpty accepts whitespace-only strings; reject those too.
            .Must(static t => !string.IsNullOrWhiteSpace(t)).WithMessage("Title is required.")
            .MaximumLength(Post.Constraints.MaxTitleLength)
                .WithMessage($"Title must be {Post.Constraints.MaxTitleLength} characters or fewer.");

        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Content is required.")
            .Must(static c => !string.IsNullOrWhiteSpace(c)).WithMessage("Content is required.")
            .MaximumLength(Post.Constraints.MaxContentLength)
                .WithMessage($"Content must be {Post.Constraints.MaxContentLength} characters or fewer.");
    }
}

public sealed class UpdatePostRequestValidator : AbstractValidator<UpdatePostRequest>
{
    public UpdatePostRequestValidator()
    {
        // Same rationale as CreatePostRequestValidator above. The two validators are
        // currently rule-for-rule identical because the DTOs share the same field shape;
        // they're kept separate (rather than abstracted behind a base class) so a future
        // divergence — e.g. PATCH-style partial updates, immutable Title — doesn't force
        // a refactor.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Title)
            .NotEmpty().WithMessage("Title is required.")
            .Must(static t => !string.IsNullOrWhiteSpace(t)).WithMessage("Title is required.")
            .MaximumLength(Post.Constraints.MaxTitleLength)
                .WithMessage($"Title must be {Post.Constraints.MaxTitleLength} characters or fewer.");

        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Content is required.")
            .Must(static c => !string.IsNullOrWhiteSpace(c)).WithMessage("Content is required.")
            .MaximumLength(Post.Constraints.MaxContentLength)
                .WithMessage($"Content must be {Post.Constraints.MaxContentLength} characters or fewer.");
    }
}
