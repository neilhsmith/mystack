using Api.Validation;

namespace Api.Tests.Unit;

public class JsonPropertyNamingTests
{
    [Theory]
    [InlineData("Title", "title")]
    [InlineData("Content", "content")]
    [InlineData("UserName", "userName")]
    [InlineData("URL", "url")]                 // all-caps acronym fully lowercases
    [InlineData("HttpStatusCode", "httpStatusCode")]
    [InlineData("alreadyCamel", "alreadyCamel")]
    [InlineData("", "")]
    public void ToJsonName_MatchesJsonNamingPolicy_CamelCase(string input, string expected)
    {
        Assert.Equal(expected, JsonPropertyNaming.ToJsonName(input));
    }

    [Theory]
    [InlineData("Address.Street", "address.street")]
    [InlineData("Tags[0]", "tags[0]")]                 // indexer left as-is on the segment
    [InlineData("Items[0].Name", "items[0].name")]
    [InlineData("Title", "title")]                     // single-segment path == ToJsonName
    [InlineData("", "")]
    public void ToJsonPath_Transforms_EachSegmentIndependently(string input, string expected)
    {
        Assert.Equal(expected, JsonPropertyNaming.ToJsonPath(input));
    }
}
