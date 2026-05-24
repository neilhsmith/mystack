using FluentValidation;

namespace Api.Features.Posts;

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
            .MaximumLength(Post.MaxTitleLength)
                .WithMessage($"Title must be {Post.MaxTitleLength} characters or fewer.");

        RuleFor(x => x.Content)
            .NotEmpty().WithMessage("Content is required.")
            .Must(static c => !string.IsNullOrWhiteSpace(c)).WithMessage("Content is required.");
    }
}
