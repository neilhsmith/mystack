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
        AssertMaxLength(schema, "title", Api.Features.Posts.Post.MaxTitleLength);
        AssertMaxLength(schema, "content", Api.Features.Posts.Post.MaxContentLength);
    }

    [Fact]
    public async Task UpdatePostRequest_Schema_AdvertisesFluentValidationConstraints()
    {
        var schema = await GetSchema("UpdatePostRequest");

        AssertRequired(schema, "title", "content");
        AssertMaxLength(schema, "title", Api.Features.Posts.Post.MaxTitleLength);
        AssertMaxLength(schema, "content", Api.Features.Posts.Post.MaxContentLength);
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
}
