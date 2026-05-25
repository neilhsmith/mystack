using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Http;

/// <summary>
/// Maps <see cref="DbUpdateConcurrencyException"/> to <c>409 Conflict</c> as
/// <c>application/problem+json</c>. The exception is thrown by EF when an <c>UPDATE</c>
/// (or <c>DELETE</c>) finds the row's <c>xmin</c> token bumped since this request loaded
/// it — i.e. another writer raced in between load and save. Surfacing it as <c>409</c>
/// lets clients react uniformly: refetch and retry.
/// <para>
/// Registered as an <see cref="IExceptionHandler"/> so the existing
/// <c>UseExceptionHandler</c> pipeline picks it up and the response flows through the
/// <c>AddProblemDetails</c> customizer (so <c>traceId</c> lands on the body, same shape
/// as every other error in the API).
/// </para>
/// </summary>
internal sealed class DbUpdateConcurrencyExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;

    public DbUpdateConcurrencyExceptionHandler(IProblemDetailsService problemDetails) =>
        _problemDetails = problemDetails;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Resource was modified by another writer.",
                Detail = "The resource changed since you last fetched it. Refetch and retry.",
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.10",
            },
        });
    }
}
