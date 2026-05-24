using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

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
    public async Task Put_UpdatesPost()
    {
        var created = await CreatePost("Original", "Original body");
        var update = new UpdatePostRequest("Updated", "Updated body");

        var response = await _client.PutAsJsonAsync($"/posts/{created.Id}", update, TestContext.Current.CancellationToken);

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
        var update = new UpdatePostRequest("title", "content");

        var response = await _client.PutAsJsonAsync($"/posts/{Guid.NewGuid()}", update, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_Returns400_WhenTitleEmpty()
    {
        var created = await CreatePost("Original", "Original body");
        var update = new UpdatePostRequest("", "body");

        var response = await _client.PutAsJsonAsync($"/posts/{created.Id}", update, TestContext.Current.CancellationToken);

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
    public async Task Delete_RemovesPost()
    {
        var created = await CreatePost("To delete", "body");

        var deleteResponse = await _client.DeleteAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_Returns404_WhenNotFound()
    {
        var response = await _client.DeleteAsync($"/posts/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<PostResponse> CreatePost(string title, string content)
    {
        var response = await _client.PostAsJsonAsync(
            "/posts",
            new CreatePostRequest(title, content),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(TestContext.Current.CancellationToken);
        return body!;
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
