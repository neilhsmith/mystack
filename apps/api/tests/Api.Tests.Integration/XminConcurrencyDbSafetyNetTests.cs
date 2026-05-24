using Api.Data;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

/// <summary>
/// Exercises the DB-level concurrency safety net (Postgres <c>xmin</c> system column wired
/// up as an EF concurrency token via the convention loop in <c>AppDbContext.OnModelCreating</c>).
/// Tests bypass the HTTP layer and drive <c>DbContext</c> directly so they prove the
/// schema/convention is configured correctly, independent of any single endpoint's wiring.
/// <para>
/// The HTTP-level catch in <c>PostsEndpoints.TryConcurrentSaveAsync</c> (which turns
/// <see cref="DbUpdateConcurrencyException"/> into 412 with the current ETag) is not
/// directly tested — exercising it requires injecting a concurrent write between the
/// handler's load and save in the same request, which needs a synchronization seam the
/// production code doesn't expose. The catch block is small and reviewable; the
/// mechanism it depends on is verified here.
/// </para>
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class XminConcurrencyDbSafetyNetTests : IAsyncLifetime
{
    private readonly ApiTestFactory _factory;

    public XminConcurrencyDbSafetyNetTests(ApiTestFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Posts.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => default;

    [Fact]
    public async Task Xmin_Advances_OnEveryUpdate()
    {
        var id = Guid.CreateVersion7();

        // Seed via raw SQL so we don't trigger EF's concurrency machinery on insert.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""Posts"" (""Id"", ""Title"", ""Content"") VALUES ({0}, {1}, {2});",
                id, "seed", "body");
        }

        var xminBefore = await ReadXmin(id);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Posts"" SET ""Title"" = {0} WHERE ""Id"" = {1};",
                "modified", id);
        }

        var xminAfter = await ReadXmin(id);

        Assert.NotEqual(xminBefore, xminAfter);
    }

    [Fact]
    public async Task Xmin_ConcurrentWriters_LoserThrowsDbUpdateConcurrencyException()
    {
        // Both writers load the same row; writer B saves first, bumping xmin in the DB.
        // Writer A's save then finds a stale concurrency token and throws.
        var id = Guid.CreateVersion7();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Posts.Add(new Post { Id = id, Title = "race seed", Content = "body" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var scopeA = _factory.Services.CreateAsyncScope();
        await using var scopeB = _factory.Services.CreateAsyncScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

        var postA = await dbA.Posts.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        var postB = await dbB.Posts.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);

        postB.Title = "B won";
        await dbB.SaveChangesAsync(TestContext.Current.CancellationToken);

        postA.Title = "A lost";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => dbA.SaveChangesAsync(TestContext.Current.CancellationToken));

        // Confirm B's value is the persisted one.
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var final = await verifyDb.Posts.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        Assert.Equal("B won", final.Title);
    }

    private async Task<uint> ReadXmin(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var post = await db.Posts.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        return (uint)db.Entry(post).Property("xmin").CurrentValue!;
    }
}
