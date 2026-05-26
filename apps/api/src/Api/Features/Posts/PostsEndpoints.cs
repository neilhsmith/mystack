using Api.Http;
using Api.Validation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Api.Features.Posts;

/// <summary>
/// HTTP-only wiring for the Posts resource. Each handler reads request inputs, calls a
/// single <see cref="PostsService"/> method, and translates the
/// <see cref="ErrorOr.ErrorOr{TValue}"/> result into an HTTP response.
/// <para>
/// The success path uses <see cref="TypedResults"/> for static response-shape inference;
/// the error path always goes through <see cref="ErrorResults.ToProblem(List{ErrorOr.Error})"/>,
/// which emits <c>application/problem+json</c> with the same envelope as the rest of the
/// API (validation filter, exception handlers, rate limiter). No <c>try</c>/<c>catch</c>,
/// no per-handler status-code logic — see <see cref="ErrorResults.StatusFor(ErrorOr.ErrorType)"/>
/// for the kind-to-status mapping.
/// </para>
/// </summary>
public static class PostsEndpoints
{
    public static IEndpointRouteBuilder MapPostsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/posts").WithTags("Posts");

        group.MapGet("/", GetAll);

        group.MapGet("/{id:guid}", GetById)
            // Service-returned errors flow through ErrorResults.ToProblem → ProblemHttpResult.
            // The 404 carries a problem+json body (not the empty body the old endpoint produced
            // via UseStatusCodePages), so advertise it explicitly to OpenAPI.
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", Create)
            .AddEndpointFilter<ValidationEndpointFilter<CreatePostRequest>>()
            .ProducesValidationProblem();

        group.MapPut("/{id:guid}", Update)
            .AddEndpointFilter<ValidationEndpointFilter<UpdatePostRequest>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:guid}", Delete)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Ok<IEnumerable<PostResponse>>> GetAll(
        PostsService service, PostMapper mapper, CancellationToken ct)
    {
        var posts = await service.GetAllAsync(ct);
        return TypedResults.Ok(posts.Select(mapper.ToResponse));
    }

    private static async Task<Results<Ok<PostResponse>, ProblemHttpResult>> GetById(
        Guid id, PostsService service, PostMapper mapper, CancellationToken ct)
    {
        var result = await service.GetByIdAsync(id, ct);
        return result.Match<Results<Ok<PostResponse>, ProblemHttpResult>>(
            post => TypedResults.Ok(mapper.ToResponse(post)),
            errors => errors.ToProblem());
    }

    private static async Task<Results<Created<PostResponse>, ProblemHttpResult>> Create(
        CreatePostRequest request, PostsService service, PostMapper mapper, CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.Match<Results<Created<PostResponse>, ProblemHttpResult>>(
            // The "/v1" prefix is owned by Program.cs's MapGroup("/v1"). Mirrored here in the
            // Location header — keep this hardcoded for now; revisit (LinkGenerator, route names,
            // or a per-feature RoutePrefix const) when a second versioned resource shows up.
            post => TypedResults.Created($"/v1/posts/{post.Id}", mapper.ToResponse(post)),
            errors => errors.ToProblem());
    }

    private static async Task<Results<Ok<PostResponse>, ProblemHttpResult>> Update(
        Guid id, UpdatePostRequest request, PostsService service, PostMapper mapper, CancellationToken ct)
    {
        var result = await service.UpdateAsync(id, request, ct);
        return result.Match<Results<Ok<PostResponse>, ProblemHttpResult>>(
            post => TypedResults.Ok(mapper.ToResponse(post)),
            errors => errors.ToProblem());
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Delete(
        Guid id, PostsService service, CancellationToken ct)
    {
        var result = await service.DeleteAsync(id, ct);
        return result.Match<Results<NoContent, ProblemHttpResult>>(
            _ => TypedResults.NoContent(),
            errors => errors.ToProblem());
    }
}
