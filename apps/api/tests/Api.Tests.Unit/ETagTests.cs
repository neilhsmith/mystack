using Api.Features.Posts;
using Api.Http;

namespace Api.Tests.Unit;

public class ETagTests
{
    [Fact]
    public void From_Timestamp_ProducesStrongTagWithQuotedHexValue()
    {
        var updatedAt = new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero);

        var tag = ETag.From(updatedAt);

        Assert.False(tag.IsWeak);
        // 16 hex chars wrapped in double quotes → length 18 (the leading/trailing ").
        var raw = tag.Tag.ToString();
        Assert.Equal(18, raw.Length);
        Assert.StartsWith("\"", raw);
        Assert.EndsWith("\"", raw);
        var hex = raw[1..^1];
        Assert.Equal(16, hex.Length);
        Assert.Matches("^[0-9A-F]{16}$", hex);
    }

    [Fact]
    public void From_Timestamp_IsDeterministic_ForSameInput()
    {
        var updatedAt = DateTimeOffset.UtcNow;

        var a = ETag.From(updatedAt);
        var b = ETag.From(updatedAt);

        Assert.Equal(a.Tag.ToString(), b.Tag.ToString());
    }

    [Fact]
    public void From_Timestamp_DiffersForDifferentInputs()
    {
        var a = ETag.From(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var b = ETag.From(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));

        Assert.NotEqual(a.Tag.ToString(), b.Tag.ToString());
    }

    [Fact]
    public void From_Timestamp_NormalizesToUtc()
    {
        // Same instant, different offsets → same UtcTicks → same ETag.
        var utc = new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero);
        var sameInstantInDifferentZone = utc.ToOffset(TimeSpan.FromHours(5));

        Assert.Equal(
            ETag.From(utc).Tag.ToString(),
            ETag.From(sameInstantInDifferentZone).Tag.ToString());
    }

    [Fact]
    public void From_Entity_DelegatesToUpdatedAt()
    {
        var updatedAt = new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero);
        var post = new Post
        {
            Title = "T",
            Content = "C",
            UpdatedAt = updatedAt,
        };

        Assert.Equal(
            ETag.From(updatedAt).Tag.ToString(),
            ETag.From(post).Tag.ToString());
    }
}
