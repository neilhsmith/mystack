using Api.Data;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

/// <summary>
/// Service-boundary tests for <see cref="PostsService"/>. The HTTP path is covered by
/// <see cref="PostsEndpointsTests"/>; this file proves the service itself owns validation,
/// so non-HTTP callers (background jobs, internal services, future test scaffolding) get
/// the same guarantees as a request that came through the endpoint pipeline.
/// <para>
/// Different boundary, different file — same rule that puts <c>TimestampsDbSafetyNetTests</c>
/// in its own file. Endpoint tests assert HTTP wire shape; these assert the service's
/// <see cref="ErrorOr{TValue}"/> contract.
/// </para>
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class PostsServiceTests : IAsyncLifetime
{
    private readonly ApiTestFactory _factory;

    public PostsServiceTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Posts.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => default;

    [Fact]
    public async Task CreateAsync_ReturnsValidationErrors_AndDoesNotWriteToDb_WhenInputInvalid()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PostsService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await service.CreateAsync(
            new CreatePostRequest("", ""),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.All(result.Errors, e => Assert.Equal(ErrorType.Validation, e.Type));
        Assert.Contains(result.Errors, e => e.Code == "title");
        Assert.Contains(result.Errors, e => e.Code == "content");

        var count = await db.Posts
            .IgnoreQueryFilters()
            .CountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsValidationErrors_BeforeNotFoundLookup_WhenIdMissingAndBodyInvalid()
    {
        // The service documents validate-before-lookup; this asserts it directly. If the
        // order ever reverses, this would surface as a NotFound error rather than Validation.
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PostsService>();

        var result = await service.UpdateAsync(
            Guid.NewGuid(),
            new UpdatePostRequest("", "body"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var firstError = result.Errors[0];
        Assert.Equal(ErrorType.Validation, firstError.Type);
        Assert.Equal("title", firstError.Code);
    }
}
