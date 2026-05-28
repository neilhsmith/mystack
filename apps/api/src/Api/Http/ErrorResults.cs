using ErrorOr;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Api.Http;

/// <summary>
/// Single source of truth for turning <see cref="ErrorOr.Error"/> values produced by the
/// service layer into <c>application/problem+json</c> responses.
/// <para>
/// Endpoint handlers do not invent status codes themselves — they call <see cref="ToProblem(List{Error})"/>
/// (or the single-error overload) and the mapping below decides the wire shape. That keeps
/// every error response across the API uniform: same problem+json envelope, same
/// <c>traceId</c> stamped by the <c>AddProblemDetails</c> customizer in <c>Program.cs</c>,
/// same <c>Type</c> URLs pointing at the RFC 9110 sections, same <c>errors</c> bag shape
/// as <see cref="Microsoft.AspNetCore.Http.Results.ValidationProblem(IDictionary{string, string[]}, string?, string?, int?, string?, string?, IDictionary{string, object?}?)"/>.
/// Services run request validation themselves (via injected <see cref="FluentValidation.IValidator{T}"/>s)
/// and return <see cref="ErrorType.Validation"/> errors keyed by JSON property path; this
/// adapter shapes those into the validation envelope clients expect.
/// </para>
/// <para>
/// Field-bound vs. global validation errors: ErrorOr's <see cref="Error.Code"/> is reused
/// as the field path key in the problem+json <c>errors</c> dictionary. Microsoft and
/// FluentValidation both use the empty string <c>""</c> for "request-level" / non-field
/// errors, so an <see cref="Error.Validation(string, string, System.Collections.Generic.Dictionary{string, object}?)"/>
/// constructed with <c>code: ""</c> lands under that key — same place a UI would already
/// be looking for global messages.
/// </para>
/// </summary>
public static class ErrorResults
{
    /// <summary>
    /// Convert a list of <see cref="Error"/>s into a <see cref="ProblemHttpResult"/>.
    /// <list type="bullet">
    ///   <item>All errors are <see cref="ErrorType.Validation"/> → <c>400</c> with the
    ///   standard <c>errors</c> bag, grouped by <see cref="Error.Code"/> (the field path,
    ///   or <c>""</c> for global).</item>
    ///   <item>Otherwise → first non-validation error wins; its <see cref="Error.Type"/>
    ///   maps to the status code (<see cref="StatusFor"/>), <see cref="Error.Description"/>
    ///   becomes the <c>title</c>.</item>
    /// </list>
    /// <para>
    /// Mixed lists (validation + non-validation in the same result) are a defensive
    /// fallback, not a designed-for path. Services should either fail fast (NotFound,
    /// Conflict, Unauthorized) or aggregate validation — not both in one call. When a
    /// mixed list does reach the adapter, the non-validation error wins because a
    /// fundamental failure ("the post doesn't exist") matters more than a shape problem
    /// on a request you can't act on anyway; the validation errors are dropped on the
    /// floor. If you genuinely need to surface both, return the validation result first
    /// and let the caller act on it before attempting the operation that produced the
    /// non-validation error.
    /// </para>
    /// </summary>
    public static ProblemHttpResult ToProblem(this List<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            throw new InvalidOperationException(
                "ToProblem called with an empty error list — caller should have returned the success value.");
        }

        if (errors.TrueForAll(static e => e.Type == ErrorType.Validation))
        {
            return BuildValidationProblem(errors);
        }

        // First non-validation error decides the status. Stable, predictable, and lets
        // services express precedence by ordering (NotFound before Validation, etc.).
        // Error is a struct in ErrorOr, so use FirstOrDefault with a fallback rather than
        // null-coalescing (Find would return default(Error) on no match, not null).
        var driver = errors.FirstOrDefault(
            static e => e.Type != ErrorType.Validation,
            defaultValue: errors[0]);
        return TypedResults.Problem(
            statusCode: StatusFor(driver.Type),
            title: driver.Description,
            type: ProblemTypeFor(driver.Type));
    }

    /// <summary>
    /// Convenience overload for the single-error case. Wraps the error in a one-element
    /// list and dispatches to <see cref="ToProblem(List{Error})"/>.
    /// </summary>
    public static ProblemHttpResult ToProblem(this Error error) =>
        new List<Error> { error }.ToProblem();

    private static ProblemHttpResult BuildValidationProblem(List<Error> errors)
    {
        // Group by Code (field path, or "" for global). Each field's array preserves the
        // service's insertion order, same as FluentValidation's ToDictionary() does.
        var bag = errors
            .GroupBy(static e => e.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray(), StringComparer.Ordinal);

        // HttpValidationProblemDetails (the minimal-APIs flavour) serializes the same wire
        // shape Results.ValidationProblem would produce, so UI code can't tell whether the
        // 400 came from a service running FluentValidation or from a service-level business
        // rule that emitted Error.Validation values.
        return TypedResults.Problem(new HttpValidationProblemDetails(bag)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1",
        });
    }

    /// <summary>
    /// Maps an <see cref="ErrorType"/> to its HTTP status code. Internal so we have exactly
    /// one place that decides "this kind of failure means this status".
    /// </summary>
    internal static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Failure => StatusCodes.Status500InternalServerError,
        ErrorType.Unexpected => StatusCodes.Status500InternalServerError,
        // Custom types (ErrorOr lets callers define their own numeric types) currently
        // collapse to 500. Add cases here if/when the codebase introduces them.
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// RFC 9110 section URLs for the <c>type</c> field on problem+json. Null when no
    /// canonical section applies (e.g. <see cref="ErrorType.Failure"/>) — ASP.NET will fall
    /// back to a default <c>about:blank</c>.
    /// </summary>
    internal static string? ProblemTypeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1",
        ErrorType.Unauthorized => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.2",
        ErrorType.Forbidden => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4",
        ErrorType.NotFound => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.5",
        ErrorType.Conflict => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.10",
        _ => null,
    };
}
