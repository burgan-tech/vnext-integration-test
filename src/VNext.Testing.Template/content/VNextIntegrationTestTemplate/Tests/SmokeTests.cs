using MyDomain.IntegrationTests.Infrastructure;

namespace MyDomain.IntegrationTests.Tests;

/// <summary>
/// Smoke tests — verify the environment is up and the API is reachable.
/// These run first and catch infrastructure issues before domain tests execute.
/// </summary>
public class SmokeTests : IntegrationTestBase
{
    public SmokeTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task HealthEndpoint_Returns200()
    {
        var (statusCode, _) = await Api.GetRawAsync("/health");
        Assert.Equal(200, statusCode);
    }

    [Fact]
    public async Task ListInstances_ReturnsValidResponse()
    {
        // Replace "my-workflow" with a workflow name that exists in your domain.
        var result = await Api.ListInstancesAsync("my-workflow");
        Assert.True(result.ValueKind != System.Text.Json.JsonValueKind.Null,
            "Expected a non-null response from ListInstances");
    }
}
