using System.Net;
using System.Text.Json;
using Api.Tests.Integration.Fixtures;

namespace Api.Tests.Integration;

/// <summary>
/// Regression guard for the generated OpenAPI document. Confirms FluentValidation rules
/// flow through into <c>/openapi/v1.json</c> so downstream consumers (e.g. a generated TS
/// client) see the same constraints the runtime enforces.
/// <para>
/// If you add a new validator rule and this test doesn't notice, the schema transformer
/// (<c>FluentValidationSchemaTransformer</c>) probably doesn't know how to map that rule
/// type yet — extend it and add a case here.
/// </para>
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class OpenApiSpecTests
{
    private readonly HttpClient _client;

    public OpenApiSpecTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task OpenApi_Document_Is_Served()
    {
        var response = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("3.1.1", doc.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task CreatePostRequest_Schema_AdvertisesFluentValidationConstraints()
    {
        var schema = await GetSchema("CreatePostRequest");

        AssertRequired(schema, "title", "content");
        AssertMaxLength(schema, "title", Api.Features.Posts.Post.Constraints.MaxTitleLength);
        AssertMaxLength(schema, "content", Api.Features.Posts.Post.Constraints.MaxContentLength);
    }

    [Fact]
    public async Task UpdatePostRequest_Schema_AdvertisesFluentValidationConstraints()
    {
        var schema = await GetSchema("UpdatePostRequest");

        AssertRequired(schema, "title", "content");
        AssertMaxLength(schema, "title", Api.Features.Posts.Post.Constraints.MaxTitleLength);
        AssertMaxLength(schema, "content", Api.Features.Posts.Post.Constraints.MaxContentLength);
    }

    // ---------- Conditional-request headers ----------

    [Fact]
    public async Task GetById_Advertises_ETag_Response_Header_And_IfNoneMatch_Parameter()
    {
        var op = await GetOperation("/posts/{id}", "get");

        AssertResponseHeader(op, "200", "ETag");
        AssertResponseHeader(op, "304", "ETag");
        AssertHeaderParameter(op, "If-None-Match", required: false);
    }

    [Fact]
    public async Task Post_Advertises_ETag_Response_Header()
    {
        var op = await GetOperation("/posts", "post");

        AssertResponseHeader(op, "201", "ETag");
    }

    [Fact]
    public async Task Put_Advertises_ETag_And_Requires_IfMatch()
    {
        var op = await GetOperation("/posts/{id}", "put");

        AssertResponseHeader(op, "200", "ETag");
        AssertResponseHeader(op, "412", "ETag");
        AssertHeaderParameter(op, "If-Match", required: true);
    }

    [Fact]
    public async Task Delete_Requires_IfMatch_And_Exposes_Current_ETag_On_412()
    {
        var op = await GetOperation("/posts/{id}", "delete");

        AssertResponseHeader(op, "412", "ETag");
        AssertHeaderParameter(op, "If-Match", required: true);
    }

    [Fact]
    public async Task Delete_204_Does_Not_Advertise_ETag()
    {
        // Sanity check: DELETE's success response is 204 No Content, and the handler
        // never sets an ETag on it. The spec must not lie about that.
        var op = await GetOperation("/posts/{id}", "delete");

        AssertNoResponseHeader(op, "204", "ETag");
    }

    private async Task<JsonElement> GetSchema(string schemaName)
    {
        var response = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        var schema = doc.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(schemaName);

        // Clone so we can dispose the JsonDocument and still inspect the schema element.
        return schema.Clone();
    }

    private static void AssertRequired(JsonElement schema, params string[] expectedProps)
    {
        Assert.True(
            schema.TryGetProperty("required", out var required),
            "Schema is missing a 'required' array");
        var actual = required.EnumerateArray().Select(e => e.GetString()).ToHashSet();
        foreach (var prop in expectedProps)
        {
            Assert.Contains(prop, actual);
        }
    }

    private static void AssertMaxLength(JsonElement schema, string propertyName, int expected)
    {
        var prop = schema.GetProperty("properties").GetProperty(propertyName);
        Assert.True(
            prop.TryGetProperty("maxLength", out var maxLength),
            $"Property '{propertyName}' is missing maxLength");
        Assert.Equal(expected, maxLength.GetInt32());
    }

    private async Task<JsonElement> GetOperation(string path, string verb)
    {
        var response = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        return doc.RootElement
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(verb)
            .Clone();
    }

    private static void AssertResponseHeader(JsonElement operation, string statusCode, string headerName)
    {
        var responses = operation.GetProperty("responses");
        Assert.True(
            responses.TryGetProperty(statusCode, out var response),
            $"Response {statusCode} is missing");
        Assert.True(
            response.TryGetProperty("headers", out var headers),
            $"Response {statusCode} has no 'headers' object");
        Assert.True(
            headers.TryGetProperty(headerName, out _),
            $"Response {statusCode} is missing the '{headerName}' header");
    }

    private static void AssertNoResponseHeader(JsonElement operation, string statusCode, string headerName)
    {
        var responses = operation.GetProperty("responses");
        if (!responses.TryGetProperty(statusCode, out var response))
        {
            return; // No response defined at all → no header to advertise.
        }
        if (!response.TryGetProperty("headers", out var headers))
        {
            return; // No headers block → header definitely absent.
        }
        Assert.False(
            headers.TryGetProperty(headerName, out _),
            $"Response {statusCode} unexpectedly advertises the '{headerName}' header");
    }

    private static void AssertHeaderParameter(JsonElement operation, string headerName, bool required)
    {
        Assert.True(
            operation.TryGetProperty("parameters", out var parameters),
            "Operation has no parameters array");

        var param = parameters.EnumerateArray()
            .FirstOrDefault(p =>
                p.GetProperty("in").GetString() == "header" &&
                p.GetProperty("name").GetString() == headerName);

        Assert.NotEqual(default, param);

        var actualRequired = param.TryGetProperty("required", out var req) && req.GetBoolean();
        Assert.Equal(required, actualRequired);
    }
}
