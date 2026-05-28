using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

/// <summary>
/// Full behaviour suite for the Posts endpoints: CRUD plus the cross-cutting concerns that
/// land on every Posts response — body-hash ETag + 304 on GETs (via EtagMiddleware),
/// soft-delete visibility, and validation. Tests are grouped by HTTP verb so each
/// endpoint's full contract reads top-to-bottom.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class PostsEndpointsTests : IAsyncLifetime
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _client;

    public PostsEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Authenticate every request as an Admin with both scopes — covers all the Posts
        // endpoint authorization requirements (mystack.read/write + posts.* permissions).
        // Tests that target the auth boundary itself (401/403 paths) override these headers
        // per-request.
        _client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, "Admin");
        _client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, "mystack.read mystack.write");
    }

    public async ValueTask InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // IgnoreQueryFilters so we also nuke any soft-deleted rows from previous tests.
        await db.Posts.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => default;

    // ---------- GET /v1/posts ----------

    [Fact]
    public async Task Get_All_Posts_ReturnsEmpty_WhenNoPosts()
    {
        var response = await _client.GetAsync("/v1/posts", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<PostResponse>>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    [Fact]
    public async Task GetAll_ReturnsPosts_OrderedByCreatedAtDescending()
    {
        var first = await CreatePost("First", "1");
        await Task.Delay(10, TestContext.Current.CancellationToken);
        var second = await CreatePost("Second", "2");
        await Task.Delay(10, TestContext.Current.CancellationToken);
        var third = await CreatePost("Third", "3");

        var response = await _client.GetAsync("/v1/posts", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<PostResponse>>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(3, body.Count);
        Assert.Equal(third.Id, body[0].Id);
        Assert.Equal(second.Id, body[1].Id);
        Assert.Equal(first.Id, body[2].Id);
    }

    [Fact]
    public async Task GetAll_DoesNotReturnSoftDeletedPosts()
    {
        var keeper = await CreatePost("Keeper", "body");
        var doomed = await CreatePost("Doomed", "body");

        var deleteResponse = await _client.DeleteAsync(
            $"/v1/posts/{doomed.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listResponse = await _client.GetAsync("/v1/posts", TestContext.Current.CancellationToken);
        var posts = await listResponse.Content.ReadFromJsonAsync<List<PostResponse>>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(posts);
        Assert.Single(posts);
        Assert.Equal(keeper.Id, posts[0].Id);
    }

    // ---------- GET /v1/posts/{id} ----------

    [Fact]
    public async Task GetById_ReturnsCreatedPost()
    {
        var created = await CreatePost("Lookup test", "Body");

        var response = await _client.GetAsync($"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Equal("Lookup test", body.Title);
    }

    [Fact]
    public async Task GetById_Returns404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/v1/posts/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_404_Body_Is_ServiceShaped_ProblemJson()
    {
        // The 404 body comes from PostsService → PostErrors.NotFound → ErrorResults.ToProblem,
        // not from UseStatusCodePages filling a bare empty response. So the title carries
        // the per-resource message ("Post {id} was not found.") rather than a generic
        // "Not Found", and traceId is stamped by the AddProblemDetails customizer.
        var missingId = Guid.NewGuid();

        var response = await _client.GetAsync(
            $"/v1/posts/{missingId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal(404, problem.Status);
        Assert.Equal($"Post {missingId} was not found.", problem.Title);
        Assert.True(problem.Extensions.TryGetValue("traceId", out var traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
    }

    [Fact]
    public async Task GetById_Sets_StrongQuotedETag_ViaMiddleware()
    {
        var created = await CreatePost("Tagged", "body");

        var response = await _client.GetAsync(
            $"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        var raw = response.Headers.ETag.ToString();
        Assert.StartsWith("\"", raw);
        Assert.EndsWith("\"", raw);
    }

    [Fact]
    public async Task GetById_With_IfNoneMatch_Matching_Returns304_WithoutBody()
    {
        var created = await CreatePost("Cached", "body");

        // First fetch to learn the body-hash ETag the middleware computes.
        var first = await _client.GetAsync($"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);
        var etag = first.Headers.ETag?.ToString();
        Assert.NotNull(etag);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{created.Id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        var bodyBytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Empty(bodyBytes);
    }

    [Fact]
    public async Task GetById_With_Stale_IfNoneMatch_Returns200_WithBodyAndCurrentETag()
    {
        var created = await CreatePost("Modified", "body");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{created.Id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"deadbeef\"");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
    }

    [Fact]
    public async Task GetById_ETag_Changes_AfterPut()
    {
        var created = await CreatePost("Before", "body");

        var beforeEtag = (await _client.GetAsync(
            $"/v1/posts/{created.Id}", TestContext.Current.CancellationToken)).Headers.ETag?.ToString();

        var put = await _client.PutAsJsonAsync(
            $"/v1/posts/{created.Id}",
            new UpdatePostRequest("After", "body"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var afterEtag = (await _client.GetAsync(
            $"/v1/posts/{created.Id}", TestContext.Current.CancellationToken)).Headers.ETag?.ToString();

        Assert.NotNull(beforeEtag);
        Assert.NotNull(afterEtag);
        Assert.NotEqual(beforeEtag, afterEtag);
    }

    // ---------- POST /v1/posts ----------

    [Fact]
    public async Task Post_CreatesAndReturnsCreatedPost()
    {
        var request = new CreatePostRequest("First post", "Hello world.");

        var response = await _client.PostAsJsonAsync("/v1/posts", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal("First post", body.Title);
        Assert.Equal("Hello world.", body.Content);
        Assert.Equal($"/v1/posts/{body.Id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Post_Returns400_WhenTitleEmpty()
    {
        var request = new CreatePostRequest("", "content");

        var response = await _client.PostAsJsonAsync("/v1/posts", request, TestContext.Current.CancellationToken);

        await AssertValidationProblem(response, nameof(CreatePostRequest.Title), "Title is required.");
        await AssertNoPostsExist();
    }

    [Fact]
    public async Task Post_Returns400_WhenContentEmpty()
    {
        var request = new CreatePostRequest("title", "   ");

        var response = await _client.PostAsJsonAsync("/v1/posts", request, TestContext.Current.CancellationToken);

        await AssertValidationProblem(response, nameof(CreatePostRequest.Content), "Content is required.");
        await AssertNoPostsExist();
    }

    [Fact]
    public async Task Post_Returns400_WhenTitleTooLong()
    {
        var request = new CreatePostRequest(new string('x', Post.Constraints.MaxTitleLength + 1), "content");

        var response = await _client.PostAsJsonAsync("/v1/posts", request, TestContext.Current.CancellationToken);

        await AssertValidationProblem(
            response,
            nameof(CreatePostRequest.Title),
            $"Title must be {Post.Constraints.MaxTitleLength} characters or fewer.");
        await AssertNoPostsExist();
    }

    [Fact]
    public async Task Post_Returns400_WhenContentTooLong()
    {
        var request = new CreatePostRequest("title", new string('x', Post.Constraints.MaxContentLength + 1));

        var response = await _client.PostAsJsonAsync("/v1/posts", request, TestContext.Current.CancellationToken);

        await AssertValidationProblem(
            response,
            nameof(CreatePostRequest.Content),
            $"Content must be {Post.Constraints.MaxContentLength} characters or fewer.");
        await AssertNoPostsExist();
    }

    [Fact]
    public async Task Post_ValidationProblem_Uses_CamelCase_Keys_OnTheWire()
    {
        // Belt-and-braces: AssertValidationProblem transforms CLR names internally, so a
        // future regression that emits PascalCase keys would still pass that helper. Hit
        // the raw JSON to lock in the on-the-wire contract.
        var request = new CreatePostRequest("", "");

        var response = await _client.PostAsJsonAsync("/v1/posts", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var errors = doc.RootElement.GetProperty("errors");

        // Keys are the JSON property names (camelCase), matching what the request body uses.
        var keys = errors.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("title", keys);
        Assert.DoesNotContain("Title", keys);
    }

    // ---------- PUT /v1/posts/{id} ----------

    [Fact]
    public async Task Put_UpdatesPost()
    {
        var created = await CreatePost("Original", "Original body");

        var response = await _client.PutAsJsonAsync(
            $"/v1/posts/{created.Id}",
            new UpdatePostRequest("Updated", "Updated body"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Equal("Updated", body.Title);
        Assert.Equal("Updated body", body.Content);
        Assert.True(body.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task Put_Returns404_WhenNotFound()
    {
        var response = await _client.PutAsJsonAsync(
            $"/v1/posts/{Guid.NewGuid()}",
            new UpdatePostRequest("title", "content"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_Returns400_WhenTitleEmpty()
    {
        var created = await CreatePost("Original", "Original body");

        var response = await _client.PutAsJsonAsync(
            $"/v1/posts/{created.Id}",
            new UpdatePostRequest("", "body"),
            TestContext.Current.CancellationToken);

        await AssertValidationProblem(response, nameof(UpdatePostRequest.Title), "Title is required.");

        var current = await _client.GetFromJsonAsync<PostResponse>(
            $"/v1/posts/{created.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        Assert.Equal("Original", current.Title);
        Assert.Equal("Original body", current.Content);
        Assert.Equal(created.UpdatedAt, current.UpdatedAt);
    }

    [Fact]
    public async Task Put_Returns400_NotNotFound_WhenInvalidBody_AndMissingId()
    {
        // Locks the order PostsService.UpdateAsync documents: validation runs before the
        // 404 lookup, so a malformed body wins over a missing id. If validation ever moved
        // back behind the lookup, this would surface as 404 instead of 400.
        var response = await _client.PutAsJsonAsync(
            $"/v1/posts/{Guid.NewGuid()}",
            new UpdatePostRequest("", "body"),
            TestContext.Current.CancellationToken);

        await AssertValidationProblem(response, nameof(UpdatePostRequest.Title), "Title is required.");
    }

    // ---------- DELETE /v1/posts/{id} ----------

    [Fact]
    public async Task Delete_RemovesPost()
    {
        var created = await CreatePost("To delete", "body");

        var deleteResponse = await _client.DeleteAsync(
            $"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_KeepsRowInDb_WithDeletedAtPopulated()
    {
        var created = await CreatePost("To soft-delete", "body");

        var deleteResponse = await _client.DeleteAsync(
            $"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // The row still exists, just hidden by the query filter — assert via IgnoreQueryFilters.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var raw = await db.Posts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(p => p.Id == created.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(raw.DeletedAt);
        Assert.True(raw.IsDeleted);
        Assert.Equal("To soft-delete", raw.Title);
    }

    [Fact]
    public async Task Delete_Returns404_WhenNotFound()
    {
        var response = await _client.DeleteAsync(
            $"/v1/posts/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Twice_SecondCallReturns404()
    {
        var created = await CreatePost("To delete twice", "body");

        var first = await _client.DeleteAsync(
            $"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Row is soft-deleted, so the global query filter hides it from the second lookup.
        var second = await _client.DeleteAsync(
            $"/v1/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<PostResponse> CreatePost(string title, string content)
    {
        var response = await _client.PostAsJsonAsync(
            "/v1/posts",
            new CreatePostRequest(title, content),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PostResponse>(
            TestContext.Current.CancellationToken))!;
    }

    private async Task AssertNoPostsExist()
    {
        var posts = await _client.GetFromJsonAsync<List<PostResponse>>(
            "/v1/posts",
            TestContext.Current.CancellationToken);
        Assert.NotNull(posts);
        Assert.Empty(posts);
    }

    /// <summary>
    /// Asserts that <paramref name="response"/> is the RFC 9457 problem+json validation
    /// response produced by <see cref="Api.Http.ErrorResults.ToProblem(List{ErrorOr.Error})"/>
    /// when <see cref="PostsService"/> returns validation errors, and that the named
    /// property's first error matches <paramref name="expectedMessage"/>. The validator
    /// is configured to stop at first failure per property, so the error array is length 1.
    /// <para>
    /// <paramref name="propertyName"/> takes the CLR name (use <c>nameof(...)</c>) so call
    /// sites stay refactor-safe; the helper transforms it to the JSON shape clients see
    /// (camelCase) via <see cref="Api.Validation.JsonPropertyNaming.ToJsonName"/>.
    /// </para>
    /// </summary>
    private static async Task AssertValidationProblem(
        HttpResponseMessage response, string propertyName, string expectedMessage)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);

        var expectedKey = Api.Validation.JsonPropertyNaming.ToJsonName(propertyName);
        Assert.True(
            problem.Errors.TryGetValue(expectedKey, out var messages),
            $"Expected validation error key '{expectedKey}', got: {string.Join(", ", problem.Errors.Keys)}");
        Assert.Contains(expectedMessage, messages!);
    }
}
