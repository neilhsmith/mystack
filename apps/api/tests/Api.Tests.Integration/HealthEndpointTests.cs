using System.Net;
using Api.Tests.Integration.Fixtures;

namespace Api.Tests.Integration;

[Collection(nameof(IntegrationTestCollection))]
public class HealthEndpointTests(ApiTestFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReturnHealthy(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Healthy", body);
    }
}
