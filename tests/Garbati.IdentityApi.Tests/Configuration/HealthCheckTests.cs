using System.Net;
using Garbati.IdentityApi.Tests.Fixtures;

namespace Garbati.IdentityApi.Tests.Configuration;

// Hosting platforms probe these without an API key, so they must stay anonymous despite the fallback policy.
[Collection("Api")]
public class HealthCheckTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_ReturnsHealthy_WithoutAnApiKey(string path)
    {
        using var client = fixture.CreateClient(clientId: null);

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
