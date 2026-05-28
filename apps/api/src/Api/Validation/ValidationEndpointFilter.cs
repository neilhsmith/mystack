using FluentValidation;

namespace Api.Validation;

/// <summary>
/// Endpoint filter that runs the registered <see cref="IValidator{T}"/> for the request
/// body parameter of type <typeparamref name="T"/> and short-circuits with
/// <c>400 Bad Request</c> (RFC 9457 problem+json via <see cref="Results.ValidationProblem"/>)
/// when validation fails.
/// <para>
/// Applied per endpoint with <c>.AddEndpointFilter&lt;ValidationEndpointFilter&lt;TRequest&gt;&gt;()</c>.
/// Validators must be discoverable via DI — see <c>Program.cs</c>'s
/// <c>AddValidatorsFromAssemblyContaining&lt;Program&gt;()</c> call.
/// </para>
/// <para>
/// Validation runs before any other endpoint logic so a malformed body never burns a
/// DB lookup or precondition check. This is the order PostsEndpoints documents.
/// </para>
/// </summary>
public sealed class ValidationEndpointFilter<T> : IEndpointFilter
    where T : class
{
    private readonly IValidator<T> _validator;

    public ValidationEndpointFilter(IValidator<T> validator)
    {
        _validator = validator;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var model = context.Arguments.OfType<T>().FirstOrDefault();
        if (model is null)
        {
            // The body parameter wasn't bound (missing or unparseable). The ASP.NET
            // pipeline normally surfaces this as a 400 before the filter runs; if it
            // somehow reaches us, fall through and let the handler deal with it.
            return await next(context);
        }

        var result = await _validator.ValidateAsync(model, context.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            // FluentValidation reports CLR property names (PascalCase), e.g. `Title`. The
            // JSON request DTO uses camelCase (`title`), so transform the keys before
            // emitting so the validation envelope matches the wire shape clients see.
            // Multiple rule failures per property are preserved as an array (matches the
            // RFC 9457 / ValidationProblemDetails shape).
            var errors = result.Errors
                .GroupBy(e => JsonPropertyNaming.ToJsonPath(e.PropertyName), StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray(),
                    StringComparer.Ordinal);

            return Results.ValidationProblem(errors);
        }

        return await next(context);
    }
}
