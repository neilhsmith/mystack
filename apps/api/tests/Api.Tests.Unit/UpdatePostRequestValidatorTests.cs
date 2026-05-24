using Api.Features.Posts;
using FluentValidation.Results;

namespace Api.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="UpdatePostRequestValidator"/>. Mirrors the create-side suite
/// because the rule set is currently identical — keep them as separate files so divergence
/// (e.g. allowing empty title on PATCH-style updates later) doesn't require splitting.
/// </summary>
public class UpdatePostRequestValidatorTests
{
    private readonly UpdatePostRequestValidator _validator = new();

    [Fact]
    public void Valid_Request_Passes()
    {
        var result = _validator.Validate(new UpdatePostRequest("Title", "Content"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_Empty_Or_Whitespace_Fails_With_RequiredMessage(string title)
    {
        var result = _validator.Validate(new UpdatePostRequest(title, "Content"));

        AssertSingleError(result, nameof(UpdatePostRequest.Title), "Title is required.");
    }

    [Fact]
    public void Title_OverMaxLength_Fails_With_MaxLengthMessage()
    {
        var overMax = new string('x', Post.MaxTitleLength + 1);

        var result = _validator.Validate(new UpdatePostRequest(overMax, "Content"));

        AssertSingleError(
            result,
            nameof(UpdatePostRequest.Title),
            $"Title must be {Post.MaxTitleLength} characters or fewer.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Content_Empty_Or_Whitespace_Fails_With_RequiredMessage(string content)
    {
        var result = _validator.Validate(new UpdatePostRequest("Title", content));

        AssertSingleError(result, nameof(UpdatePostRequest.Content), "Content is required.");
    }

    [Fact]
    public void Content_OverMaxLength_Fails_With_MaxLengthMessage()
    {
        var overMax = new string('x', Post.MaxContentLength + 1);

        var result = _validator.Validate(new UpdatePostRequest("Title", overMax));

        AssertSingleError(
            result,
            nameof(UpdatePostRequest.Content),
            $"Content must be {Post.MaxContentLength} characters or fewer.");
    }

    private static void AssertSingleError(ValidationResult result, string property, string expectedMessage)
    {
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.PropertyName == property);
        Assert.Equal(expectedMessage, error.ErrorMessage);
    }
}
