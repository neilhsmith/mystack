using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

/// <summary>
/// Full behaviour suite for the Posts endpoints: CRUD plus RFC 7232 conditional-request
/// semantics (ETag on responses, 304 on If-None-Match, 428/412 on missing/stale If-Match for
/// writes). Tests are grouped by HTTP verb so each endpoint's full contract is read top-to-bottom.
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
    }

    public async ValueTask InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // IgnoreQueryFilters so we also nuke any soft-deleted rows from previous tests.
        await db.Posts.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => default;

    // ---------- GET /posts ----------

    [Fact]
    public async Task Get_All_Posts_ReturnsEmpty_WhenNoPosts()
    {
        var response = await _client.GetAsync("/posts", TestContext.Current.CancellationToken);

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

        var response = await _client.GetAsync("/posts", TestContext.Current.CancellationToken);

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
        var (doomed, doomedEtag) = await CreatePostWithETag("Doomed", "body");

        await SendDelete(doomed.Id, doomedEtag);

        var listResponse = await _client.GetAsync("/posts", TestContext.Current.CancellationToken);
        var posts = await listResponse.Content.ReadFromJsonAsync<List<PostResponse>>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(posts);
        Assert.Single(posts);
        Assert.Equal(keeper.Id, posts[0].Id);
    }

    // ---------- GET /posts/{id} ----------

    [Fact]
    public async Task GetById_ReturnsCreatedPost()
    {
        var created = await CreatePost("Lookup test", "Body");

        var response = await _client.GetAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Equal("Lookup test", body.Title);
    }

    [Fact]
    public async Task GetById_Returns404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/posts/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_Returns_ETag_Header_MatchingPostResponse()
    {
        var (created, postEtag) = await CreatePostWithETag("Tagged", "body");

        var response = await _client.GetAsync(
            $"/posts/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.Equal(postEtag, response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task GetById_With_IfNoneMatch_Matching_Returns304_WithoutBody()
    {
        var (created, etag) = await CreatePostWithETag("Cached", "body");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/posts/{created.Id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        var bodyBytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Empty(bodyBytes);
    }

    [Fact]
    public async Task GetById_With_Stale_IfNoneMatch_Returns200_WithBodyAndCurrentETag()
    {
        var (created, _) = await CreatePostWithETag("Modified", "body");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/posts/{created.Id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"0000000000000000\"");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
    }

    // ---------- POST /posts ----------

    [Fact]
    public async Task Post_CreatesAndReturnsCreatedPost()
    {
        var request = new CreatePostRequest("First post", "Hello world.");

        var response = await _client.PostAsJsonAsync("/posts", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal("First post", body.Title);
        Assert.Equal("Hello world.", body.Content);
        Assert.Equal($"/posts/{body.Id}", response.Headers.Location?.OriginalString);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Post_ReturnsStrongQuotedETag()
    {
        var response = await _client.PostAsJsonAsync(
            "/posts",
            new CreatePostRequest("Tagged", "body"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        var raw = response.Headers.ETag.ToString();
        Assert.StartsWith("\"", raw);
        Assert.EndsWith("\"", raw);
    }

    [Fact]
    public async Task Post_Returns400_WhenTitleEmpty()
    {
        var request = new CreatePostRequest("", "content");

        var response = await _client.PostAsJsonAsync("/posts", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoPostsExist();
    }

    [Fact]
    public async Task Post_Returns400_WhenContentEmpty()
    {
        var request = new CreatePostRequest("title", "   ");

        var response = await _client.PostAsJsonAsync("/posts", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoPostsExist();
    }

    [Fact]
    public async Task Post_Returns400_WhenTitleTooLong()
    {
        var request = new CreatePostRequest(new string('x', 201), "content");

        var response = await _client.PostAsJsonAsync("/posts", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoPostsExist();
    }

    // ---------- PUT /posts/{id} ----------

    [Fact]
    public async Task Put_UpdatesPost()
    {
        var (created, etag) = await CreatePostWithETag("Original", "Original body");

        var response = await SendPut(created.Id, new UpdatePostRequest("Updated", "Updated body"), etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(created.Id, body.Id);
        Assert.Equal("Updated", body.Title);
        Assert.Equal("Updated body", body.Content);
        Assert.True(body.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task Put_Returns_New_ETag_After_Update()
    {
        var (created, originalEtag) = await CreatePostWithETag("Original", "body");

        var response = await SendPut(created.Id, new UpdatePostRequest("Updated", "updated body"), originalEtag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newEtag = response.Headers.ETag?.ToString();
        Assert.NotNull(newEtag);
        Assert.NotEqual(originalEtag, newEtag);

        // A subsequent GET returns the same new ETag.
        var get = await _client.GetAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(newEtag, get.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Put_Returns404_WhenNotFound()
    {
        var response = await SendPut(
            Guid.NewGuid(),
            new UpdatePostRequest("title", "content"),
            ifMatch: "\"deadbeef\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_Returns400_WhenTitleEmpty()
    {
        var (created, etag) = await CreatePostWithETag("Original", "Original body");

        var response = await SendPut(created.Id, new UpdatePostRequest("", "body"), etag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var current = await _client.GetFromJsonAsync<PostResponse>(
            $"/posts/{created.Id}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        Assert.Equal("Original", current.Title);
        Assert.Equal("Original body", current.Content);
        Assert.Equal(created.UpdatedAt, current.UpdatedAt);
    }

    [Fact]
    public async Task Put_Without_IfMatch_Returns428()
    {
        var (created, _) = await CreatePostWithETag("Original", "body");

        var response = await _client.PutAsJsonAsync(
            $"/posts/{created.Id}",
            new UpdatePostRequest("New", "new body"),
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status428PreconditionRequired, (int)response.StatusCode);
    }

    [Fact]
    public async Task Put_With_Stale_IfMatch_Returns412_AndExposesCurrentETag()
    {
        var (created, originalEtag) = await CreatePostWithETag("Original", "body");

        // First update — succeeds, ETag changes.
        var firstUpdate = await SendPut(created.Id, new UpdatePostRequest("First", "1"), originalEtag);
        Assert.Equal(HttpStatusCode.OK, firstUpdate.StatusCode);
        var currentEtag = firstUpdate.Headers.ETag?.ToString();
        Assert.NotNull(currentEtag);
        Assert.NotEqual(originalEtag, currentEtag);

        // Second update with the stale tag — 412.
        var stale = await SendPut(created.Id, new UpdatePostRequest("Second", "2"), originalEtag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(currentEtag, stale.Headers.ETag?.ToString());

        // Body wasn't mutated by the failed PUT.
        var current = await _client.GetFromJsonAsync<PostResponse>(
            $"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        Assert.Equal("First", current.Title);
    }

    [Fact]
    public async Task Put_With_WildcardIfMatch_Succeeds()
    {
        var (created, _) = await CreatePostWithETag("Original", "body");

        var response = await SendPut(created.Id, new UpdatePostRequest("Wildcarded", "w"), ifMatch: "*");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("Wildcarded", body.Title);
    }

    // ---------- DELETE /posts/{id} ----------

    [Fact]
    public async Task Delete_RemovesPost()
    {
        var (created, etag) = await CreatePostWithETag("To delete", "body");

        var deleteResponse = await SendDelete(created.Id, etag);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_KeepsRowInDb_WithDeletedAtPopulated()
    {
        var (created, etag) = await CreatePostWithETag("To soft-delete", "body");

        var deleteResponse = await SendDelete(created.Id, etag);
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
        var response = await SendDelete(Guid.NewGuid(), ifMatch: "\"deadbeef\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Twice_SecondCallReturns404()
    {
        var (created, etag) = await CreatePostWithETag("To delete twice", "body");

        var first = await SendDelete(created.Id, etag);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Second call: row is soft-deleted, so the query filter hides it. 404 wins
        // before the If-Match check, so the original ETag is fine to send here.
        var second = await SendDelete(created.Id, etag);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Delete_Without_IfMatch_Returns428_AndDoesNotRemovePost()
    {
        var (created, _) = await CreatePostWithETag("Persistent", "body");

        var response = await _client.DeleteAsync(
            $"/posts/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status428PreconditionRequired, (int)response.StatusCode);

        var get = await _client.GetAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Delete_With_Stale_IfMatch_Returns412_AndDoesNotRemovePost()
    {
        var (created, originalEtag) = await CreatePostWithETag("Persistent", "body");

        var update = await SendPut(created.Id, new UpdatePostRequest("Bumped", "b"), originalEtag);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var stale = await SendDelete(created.Id, originalEtag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var get = await _client.GetAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Delete_With_WildcardIfMatch_SoftDeletes()
    {
        var (created, _) = await CreatePostWithETag("Wildcard delete", "body");

        var response = await SendDelete(created.Id, ifMatch: "*");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var get = await _client.GetAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // ---------- helpers ----------

    private async Task<PostResponse> CreatePost(string title, string content) =>
        (await CreatePostWithETag(title, content)).post;

    private async Task<(PostResponse post, string etag)> CreatePostWithETag(string title, string content)
    {
        var response = await _client.PostAsJsonAsync(
            "/posts",
            new CreatePostRequest(title, content),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        var etag = response.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("Server did not return an ETag header on POST.");
        return (body!, etag);
    }

    private async Task<HttpResponseMessage> SendPut(Guid id, UpdatePostRequest payload, string ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/posts/{id}")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> SendDelete(Guid id, string ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/posts/{id}");
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task AssertNoPostsExist()
    {
        var posts = await _client.GetFromJsonAsync<List<PostResponse>>(
            "/posts",
            TestContext.Current.CancellationToken);
        Assert.NotNull(posts);
        Assert.Empty(posts);
    }
}
