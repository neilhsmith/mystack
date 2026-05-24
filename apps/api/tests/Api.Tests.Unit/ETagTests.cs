using Api.Http;

namespace Api.Tests.Unit;

/// <summary>
/// Unit coverage for the primitive <see cref="ETag.From(uint)"/> form (xmin → strong
/// quoted hex tag). The contextual overload <see cref="ETag.From(Microsoft.EntityFrameworkCore.DbContext, object)"/>
/// needs a real <c>DbContext</c> to fish the shadow property and is exercised end-to-end
/// by <c>PostsEndpointsTests</c>.
/// </summary>
public class ETagTests
{
    [Fact]
    public void From_Xmin_ProducesStrongTagWithQuotedHexValue()
    {
        var tag = ETag.From(0x12A8u);

        Assert.False(tag.IsWeak);
        // 8 hex chars wrapped in double quotes → length 10 (the leading/trailing ").
        var raw = tag.Tag.ToString();
        Assert.Equal(10, raw.Length);
        Assert.StartsWith("\"", raw);
        Assert.EndsWith("\"", raw);
        var hex = raw[1..^1];
        Assert.Equal(8, hex.Length);
        Assert.Matches("^[0-9A-F]{8}$", hex);
    }

    [Fact]
    public void From_Xmin_IsDeterministic_ForSameInput()
    {
        var a = ETag.From(42u);
        var b = ETag.From(42u);

        Assert.Equal(a.Tag.ToString(), b.Tag.ToString());
    }

    [Fact]
    public void From_Xmin_DiffersForDifferentInputs()
    {
        var a = ETag.From(100u);
        var b = ETag.From(101u);

        Assert.NotEqual(a.Tag.ToString(), b.Tag.ToString());
    }

    [Fact]
    public void From_Xmin_ZeroPadsToEightHexChars()
    {
        // Boundary check — small values must not collide via short representations.
        var one = ETag.From(1u);
        var sixteen = ETag.From(16u);

        Assert.Equal("\"00000001\"", one.Tag.ToString());
        Assert.Equal("\"00000010\"", sixteen.Tag.ToString());
    }

    [Fact]
    public void From_Xmin_HandlesMaxUInt()
    {
        var max = ETag.From(uint.MaxValue);

        Assert.Equal("\"FFFFFFFF\"", max.Tag.ToString());
    }
}
