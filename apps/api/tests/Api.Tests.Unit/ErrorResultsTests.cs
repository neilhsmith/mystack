using Api.Http;
using ErrorOr;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Api.Tests.Unit;

/// <summary>
/// Unit coverage for the <see cref="ErrorResults.ToProblem(List{Error})"/> adapter — the
/// single source of truth for "service-returned <see cref="Error"/> → <c>problem+json</c>".
/// Endpoint integration tests prove the wire shape end-to-end; these tests pin the
/// kind-to-status mapping and the validation-aggregation rules so a regression here can be
/// spotted in sub-second feedback rather than at the API boundary.
/// </summary>
public class ErrorResultsTests
{
    // ---------- kind → status mapping ----------

    [Theory]
    [InlineData(nameof(Error.Validation), StatusCodes.Status400BadRequest)]
    [InlineData(nameof(Error.Unauthorized), StatusCodes.Status401Unauthorized)]
    [InlineData(nameof(Error.Forbidden), StatusCodes.Status403Forbidden)]
    [InlineData(nameof(Error.NotFound), StatusCodes.Status404NotFound)]
    [InlineData(nameof(Error.Conflict), StatusCodes.Status409Conflict)]
    [InlineData(nameof(Error.Failure), StatusCodes.Status500InternalServerError)]
    [InlineData(nameof(Error.Unexpected), StatusCodes.Status500InternalServerError)]
    public void Single_Error_Maps_To_Expected_Status(string factory, int expectedStatus)
    {
        var error = factory switch
        {
            nameof(Error.Validation) => Error.Validation("field", "msg"),
            nameof(Error.Unauthorized) => Error.Unauthorized("auth.missing", "Auth required."),
            nameof(Error.Forbidden) => Error.Forbidden("auth.forbidden", "No access."),
            nameof(Error.NotFound) => Error.NotFound("post.not_found", "Not found."),
            nameof(Error.Conflict) => Error.Conflict("post.duplicate", "Duplicate."),
            nameof(Error.Failure) => Error.Failure("post.save_failed", "Save failed."),
            nameof(Error.Unexpected) => Error.Unexpected("bug", "Unexpected."),
            _ => throw new ArgumentException("Unknown factory", nameof(factory)),
        };

        var problem = error.ToProblem();

        Assert.Equal(expectedStatus, problem.StatusCode);
        Assert.Equal(expectedStatus, problem.ProblemDetails.Status);
    }

    [Fact]
    public void NotFound_Uses_Description_As_Title()
    {
        var error = Error.NotFound("post.not_found", "Post abc was not found.");

        var problem = error.ToProblem();

        Assert.Equal("Post abc was not found.", problem.ProblemDetails.Title);
    }

    [Fact]
    public void NotFound_Sets_RFC_9110_Type_Url()
    {
        var problem = Error.NotFound("x.not_found", "missing").ToProblem();

        Assert.Equal(
            "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.5",
            problem.ProblemDetails.Type);
    }

    // ---------- validation-only → 400 + errors bag ----------

    [Fact]
    public void All_Validation_Errors_Produce_400_With_Errors_Bag_Grouped_By_Code()
    {
        var errors = new List<Error>
        {
            Error.Validation("title", "Title is required."),
            Error.Validation("title", "Title must be ≤ 200 chars."),
            Error.Validation("content", "Content is required."),
        };

        var problem = errors.ToProblem();

        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var validation = Assert.IsType<HttpValidationProblemDetails>(problem.ProblemDetails);
        Assert.Equal(2, validation.Errors.Count);
        Assert.Equal(
            new[] { "Title is required.", "Title must be ≤ 200 chars." },
            validation.Errors["title"]);
        Assert.Equal(new[] { "Content is required." }, validation.Errors["content"]);
    }

    [Fact]
    public void Global_Validation_Error_Lands_Under_Empty_String_Key()
    {
        // The Microsoft / FluentValidation convention for "request-level" errors with no
        // field association is the empty string key. ErrorOr.Error.Code = "" rides on the
        // same convention.
        var errors = new List<Error>
        {
            Error.Validation(code: string.Empty, description: "Title and content cannot match."),
        };

        var problem = errors.ToProblem();

        var validation = Assert.IsType<HttpValidationProblemDetails>(problem.ProblemDetails);
        Assert.True(validation.Errors.ContainsKey(string.Empty));
        Assert.Equal(
            new[] { "Title and content cannot match." },
            validation.Errors[string.Empty]);
    }

    [Fact]
    public void Validation_Only_Sets_Standard_Title_And_Type()
    {
        var problem = new List<Error> { Error.Validation("field", "msg") }.ToProblem();

        Assert.Equal("One or more validation errors occurred.", problem.ProblemDetails.Title);
        Assert.Equal(
            "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1",
            problem.ProblemDetails.Type);
    }

    // ---------- mixed lists: non-validation wins ----------

    [Fact]
    public void Mixed_List_With_NotFound_And_Validation_Returns_404_From_NotFound()
    {
        // Unusual shape (services normally fail fast or aggregate validation), but if it
        // happens we want predictable behaviour: the non-validation error drives the
        // status, the validation errors are dropped on the floor. Documented in
        // ErrorResults.ToProblem's XML doc.
        var errors = new List<Error>
        {
            Error.Validation("title", "Too long."),
            Error.NotFound("post.not_found", "Post not found."),
        };

        var problem = errors.ToProblem();

        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("Post not found.", problem.ProblemDetails.Title);
        Assert.IsNotType<HttpValidationProblemDetails>(problem.ProblemDetails);
    }

    // ---------- edge cases ----------

    [Fact]
    public void Empty_List_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new List<Error>().ToProblem());

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_List_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((List<Error>)null!).ToProblem());
    }

    [Fact]
    public void Single_Error_Overload_Equivalent_To_OneElement_List()
    {
        var error = Error.Conflict("post.duplicate_title", "Title in use.");

        var fromSingle = error.ToProblem();
        var fromList = new List<Error> { error }.ToProblem();

        Assert.Equal(fromList.StatusCode, fromSingle.StatusCode);
        Assert.Equal(fromList.ProblemDetails.Title, fromSingle.ProblemDetails.Title);
        Assert.Equal(fromList.ProblemDetails.Type, fromSingle.ProblemDetails.Type);
    }
}
