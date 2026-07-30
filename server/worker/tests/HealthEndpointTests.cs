using System.Net;
using Shouldly;

namespace MyStack.Worker.Tests;

public sealed class HealthEndpointTests(WorkerAppFixture app)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_Answer(string path)
    {
        var response = await app.Client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
