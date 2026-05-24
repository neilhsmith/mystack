using Api.Features.Posts;
using FluentValidation.Results;

namespace Api.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="CreatePostRequestValidator"/>. These cover the validator in
/// isolation — endpoint-level wiring (the filter short-circuiting with 400/RFC 9457
/// problem+json) is exercised by PostsEndpointsTests in the integration suite.
/// </summary>
public class CreatePostRequestValidatorTests
{
    private readonly CreatePostRequestValidator _validator = new();

    [Fact]
    public void Valid_Request_Passes()
    {
        var result = _validator.Validate(new CreatePostRequest("Title", "Content"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Title_Empty_Or_Whitespace_Fails_With_RequiredMessage(string title)
    {
        var result = _validator.Validate(new CreatePostRequest(title, "Content"));

        AssertSingleError(result, nameof(CreatePostRequest.Title), "Title is required.");
    }

    [Fact]
    public void Title_AtMaxLength_Passes()
    {
        var atMax = new string('x', Post.Constraints.MaxTitleLength);

        var result = _validator.Validate(new CreatePostRequest(atMax, "Content"));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Title_OverMaxLength_Fails_With_MaxLengthMessage()
    {
        var overMax = new string('x', Post.Constraints.MaxTitleLength + 1);

        var result = _validator.Validate(new CreatePostRequest(overMax, "Content"));

        AssertSingleError(
            result,
            nameof(CreatePostRequest.Title),
            $"Title must be {Post.Constraints.MaxTitleLength} characters or fewer.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Content_Empty_Or_Whitespace_Fails_With_RequiredMessage(string content)
    {
        var result = _validator.Validate(new CreatePostRequest("Title", content));

        AssertSingleError(result, nameof(CreatePostRequest.Content), "Content is required.");
    }

    [Fact]
    public void Content_AtMaxLength_Passes()
    {
        var atMax = new string('x', Post.Constraints.MaxContentLength);

        var result = _validator.Validate(new CreatePostRequest("Title", atMax));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Content_OverMaxLength_Fails_With_MaxLengthMessage()
    {
        var overMax = new string('x', Post.Constraints.MaxContentLength + 1);

        var result = _validator.Validate(new CreatePostRequest("Title", overMax));

        AssertSingleError(
            result,
            nameof(CreatePostRequest.Content),
            $"Content must be {Post.Constraints.MaxContentLength} characters or fewer.");
    }

    private static void AssertSingleError(ValidationResult result, string property, string expectedMessage)
    {
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors, e => e.PropertyName == property);
        Assert.Equal(expectedMessage, error.ErrorMessage);
    }
}
