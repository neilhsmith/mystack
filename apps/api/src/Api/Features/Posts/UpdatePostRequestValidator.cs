using FluentValidation;

namespace Api.Features.Posts;

public sealed class UpdatePostRequestValidator : AbstractValidator<UpdatePostRequest>
{
    public UpdatePostRequestValidator()
    {
        // Same rationale as CreatePostRequestValidator — see that file's comments.
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
