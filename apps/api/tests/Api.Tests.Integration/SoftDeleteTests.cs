using System.Net;
using System.Net.Http.Json;
using Api.Data;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

/// <summary>
/// Verifies the soft-delete machinery: the DELETE endpoint converts to an UPDATE that sets
/// DeletedAt; soft-deleted rows are invisible to default queries; they're still in the table
/// when query filters are bypassed.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class SoftDeleteTests : IAsyncLifetime
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _client;

    public SoftDeleteTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async ValueTask InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // IgnoreQueryFilters so we wipe soft-deleted rows from previous tests too.
        await db.Posts.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => default;

    [Fact]
    public async Task Delete_SoftDeletes_KeepsRowWithDeletedAtPopulated()
    {
        var created = await CreatePost("To soft-delete", "body");

        var deleteResponse = await _client.DeleteAsync(
            $"/posts/{created.Id}", TestContext.Current.CancellationToken);
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
    public async Task GetById_AfterDelete_Returns404()
    {
        var created = await CreatePost("To delete", "body");

        await _client.DeleteAsync($"/posts/{created.Id}", TestContext.Current.CancellationToken);
        var getResponse = await _client.GetAsync(
            $"/posts/{created.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task GetAll_DoesNotReturnSoftDeletedPosts()
    {
        var keeper = await CreatePost("Keeper", "body");
        var doomed = await CreatePost("Doomed", "body");

        await _client.DeleteAsync($"/posts/{doomed.Id}", TestContext.Current.CancellationToken);

        var listResponse = await _client.GetAsync("/posts", TestContext.Current.CancellationToken);
        var posts = await listResponse.Content.ReadFromJsonAsync<List<PostResponse>>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(posts);
        Assert.Single(posts);
        Assert.Equal(keeper.Id, posts[0].Id);
    }

    [Fact]
    public async Task Delete_Twice_SecondCallReturns404()
    {
        var created = await CreatePost("To delete twice", "body");

        var first = await _client.DeleteAsync(
            $"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await _client.DeleteAsync(
            $"/posts/{created.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    private async Task<PostResponse> CreatePost(string title, string content)
    {
        var response = await _client.PostAsJsonAsync(
            "/posts",
            new CreatePostRequest(title, content),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PostResponse>(
            TestContext.Current.CancellationToken);
        return body!;
    }
}
