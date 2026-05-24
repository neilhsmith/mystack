using System.Net;
using System.Net.Http.Json;
using Api.Tests.Integration.Fixtures;

namespace Api.Tests.Integration;

[Collection(nameof(IntegrationTestCollection))]
public class HelloEndpointTests(ApiTestFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Get_Hello_ReturnsExpectedMessage()
    {
        var response = await _client.GetAsync("/v1/hello", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<HelloResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("hello from mystack", body.Message);
    }

    private sealed record HelloResponse(string Message);
}
