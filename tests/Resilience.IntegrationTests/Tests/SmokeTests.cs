using Resilience.IntegrationTests.Infrastructure;

namespace Resilience.IntegrationTests.Tests;

/// <summary>
/// Smoke tests — verify the environment is up, the API is reachable, and the
/// resilience domain components were published.
/// </summary>
public class SmokeTests : IntegrationTestBase
{
    public SmokeTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var response = await Api.GetRawAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListInstances_OnPublishedWorkflow_ReturnsValidResponse()
    {
        var response = await Api.ListInstancesAsync(Workflow);
        Assert.True(response.Body.ValueKind != System.Text.Json.JsonValueKind.Null,
            "Expected a non-null response from ListInstances — was the resilience domain published?");
    }
}
