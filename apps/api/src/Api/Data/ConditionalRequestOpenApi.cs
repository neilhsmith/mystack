using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Api.Data;

/// <summary>
/// Stamps RFC 7232 conditional-request metadata onto an endpoint so the OpenAPI spec
/// advertises the <c>ETag</c> response header and the <c>If-Match</c> / <c>If-None-Match</c>
/// request parameters. The actual runtime behaviour lives in <see cref="ConditionalRequest"/>;
/// these extensions exist purely to keep the published contract honest so generated
/// clients (e.g. a future TS client) see the conditional-request surface area without
/// hand-written client patches.
/// <para>
/// Pick the variant that matches the endpoint:
/// </para>
/// <list type="bullet">
///   <item><see cref="WithEtagResponseHeader"/> — emits <c>ETag</c> on success responses
///   but does not itself honour request preconditions. POST that creates a resource.</item>
///   <item><see cref="WithConditionalRead"/> — emits <c>ETag</c> on 200 / 304 and accepts
///   an optional <c>If-None-Match</c> header. GET on a single resource.</item>
///   <item><see cref="WithConditionalWrite"/> — requires <c>If-Match</c> and emits the
///   current <c>ETag</c> on 200 (PUT) and on 412 (so clients can recover). PUT / DELETE.</item>
/// </list>
/// </summary>
public static class ConditionalRequestOpenApiExtensions
{
    public static RouteHandlerBuilder WithEtagResponseHeader(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new ConditionalRequestMarker(ConditionalRequestKind.EtagResponse));

    public static RouteHandlerBuilder WithConditionalRead(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new ConditionalRequestMarker(ConditionalRequestKind.Read));

    public static RouteHandlerBuilder WithConditionalWrite(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new ConditionalRequestMarker(ConditionalRequestKind.Write));
}

internal enum ConditionalRequestKind
{
    EtagResponse,
    Read,
    Write,
}

internal sealed record ConditionalRequestMarker(ConditionalRequestKind Kind);

/// <summary>
/// Reads <see cref="ConditionalRequestMarker"/> off the endpoint's metadata and writes
/// the equivalent OpenAPI shape onto the operation — ETag response headers and
/// If-Match / If-None-Match request parameters. Endpoints without the marker are
/// skipped, so registering this transformer is global and side-effect-free.
/// </summary>
internal sealed class ConditionalRequestOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var marker = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<ConditionalRequestMarker>()
            .FirstOrDefault();
        if (marker is null)
        {
            return Task.CompletedTask;
        }

        // Each kind enumerates the exact responses that carry an ETag so we don't
        // accidentally advertise the header on, say, DELETE's 204 (which has no body
        // and no ETag in the actual response).
        switch (marker.Kind)
        {
            case ConditionalRequestKind.EtagResponse:
                AddEtagToResponse(operation, "200");
                AddEtagToResponse(operation, "201");
                break;
            case ConditionalRequestKind.Read:
                AddEtagToResponse(operation, "200");
                AddEtagToResponse(operation, "304");
                AddHeaderParameter(
                    operation,
                    name: "If-None-Match",
                    required: false,
                    description: "ETag from a prior response. If it still matches the current resource, the server returns 304 with no body.");
                break;
            case ConditionalRequestKind.Write:
                AddEtagToResponse(operation, "200");
                AddEtagToResponse(operation, "412");
                AddHeaderParameter(
                    operation,
                    name: "If-Match",
                    required: true,
                    description: "ETag of the representation you intend to mutate. Missing → 428 Precondition Required; stale → 412 Precondition Failed (with the current ETag on the response).");
                break;
        }

        return Task.CompletedTask;
    }

    private static void AddEtagToResponse(OpenApiOperation operation, string statusCode)
    {
        if (operation.Responses is null)
        {
            return;
        }

        if (operation.Responses.TryGetValue(statusCode, out var response) && response is OpenApiResponse mutable)
        {
            SetEtagHeader(mutable);
        }
    }

    private static void SetEtagHeader(OpenApiResponse response)
    {
        response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
        response.Headers["ETag"] = new OpenApiHeader
        {
            Description = "Strong RFC 7232 entity tag identifying this representation. Use with If-Match on subsequent writes or If-None-Match on subsequent reads.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        };
    }

    private static void AddHeaderParameter(
        OpenApiOperation operation, string name, bool required, string description)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
    }

}
