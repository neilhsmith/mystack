using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Api.Tests.Integration;

public class HelloEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Get_Hello_ReturnsExpectedMessage()
    {
        var response = await _client.GetAsync("/hello", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<HelloResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal("hello from mystack", body.Message);
    }

    private sealed record HelloResponse(string Message);
}
