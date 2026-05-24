using Api.Data;
using Api.Features.Posts;
using Api.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Api.Tests.Integration;

/// <summary>
/// Exercises the DB-level safety net (column DEFAULT now() + UPDATE trigger) by writing
/// directly against the connection — bypassing the EF interceptor — to simulate what a
/// non-EF writer (a serverless function, a Hangfire job, a maintenance script) would do.
/// The interceptor is the normal path; these tests prove the DB still does the right
/// thing when nobody else is.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class TimestampsDbSafetyNetTests : IAsyncLifetime
{
    private readonly ApiTestFactory _factory;

    public TimestampsDbSafetyNetTests(ApiTestFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Posts.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => default;

    [Fact]
    public async Task ColumnDefault_StampsTimestamps_OnInsertWithoutThem()
    {
        var id = Guid.CreateVersion7();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Raw INSERT that omits both timestamp columns — simulates a non-EF writer.
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""Posts"" (""Id"", ""Title"", ""Content"") VALUES ({0}, {1}, {2});",
                id, "raw-insert", "body");
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
            Assert.NotEqual(default, post.CreatedAt);
            Assert.NotEqual(default, post.UpdatedAt);
        }
    }

    [Fact]
    public async Task Trigger_StampsUpdatedAt_OnUpdateWithoutIt()
    {
        var id = Guid.CreateVersion7();
        DateTimeOffset originalUpdatedAt;

        // Seed via raw SQL so we know both timestamps come from the DB clock.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""Posts"" (""Id"", ""Title"", ""Content"") VALUES ({0}, {1}, {2});",
                id, "to-update", "body");
            originalUpdatedAt = (await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken)).UpdatedAt;
        }

        // Tiny pause so now() advances past the original.
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // Raw UPDATE that doesn't touch UpdatedAt — simulates a non-EF writer modifying a row.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Posts"" SET ""Title"" = {0} WHERE ""Id"" = {1};",
                "updated-title", id);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
            Assert.True(post.UpdatedAt > originalUpdatedAt,
                $"Trigger should have bumped UpdatedAt. Original={originalUpdatedAt:O}, current={post.UpdatedAt:O}");
        }
    }

    [Fact]
    public async Task Trigger_LeavesCallerProvidedUpdatedAtAlone()
    {
        var id = Guid.CreateVersion7();
        var explicitTime = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""Posts"" (""Id"", ""Title"", ""Content"") VALUES ({0}, {1}, {2});",
                id, "to-update", "body");
        }

        // UPDATE that DOES change UpdatedAt — trigger should respect it.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Posts"" SET ""Title"" = {0}, ""UpdatedAt"" = {1} WHERE ""Id"" = {2};",
                "updated-title", explicitTime, id);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
            Assert.Equal(explicitTime, post.UpdatedAt);
        }
    }

    [Fact]
    public async Task Trigger_LocksCreatedAt_AgainstUpdates()
    {
        var id = Guid.CreateVersion7();
        DateTimeOffset originalCreatedAt;

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""Posts"" (""Id"", ""Title"", ""Content"") VALUES ({0}, {1}, {2});",
                id, "to-update", "body");
            originalCreatedAt = (await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken)).CreatedAt;
        }

        // Try to mutate CreatedAt — trigger should silently revert it.
        var attemptedNewCreatedAt = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Posts"" SET ""CreatedAt"" = {0} WHERE ""Id"" = {1};",
                attemptedNewCreatedAt, id);
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var post = await db.Posts.AsNoTracking().SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
            Assert.Equal(originalCreatedAt, post.CreatedAt);
        }
    }
}
