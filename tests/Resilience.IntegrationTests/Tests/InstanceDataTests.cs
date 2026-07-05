using Resilience.IntegrationTests.Infrastructure;

namespace Resilience.IntegrationTests.Tests;

/// <summary>
/// Instance data visibility under latest-only instance loading
/// (WorkflowExecution:LatestOnlyInstanceLoading=true). Instance data is immutable and
/// versioned; with latest-only loading the runtime materialises only the newest version
/// on the hot path — start attributes and task outputs must still merge and stay visible.
/// </summary>
public class InstanceDataTests : IntegrationTestBase
{
    public InstanceDataTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task StartAttributes_AndTaskOutput_BothVisibleOnLatestData()
    {
        var orderId = $"ORD-{Guid.NewGuid():N}"[..12];
        var id = await StartInstanceAsync(new { orderId, amount = 99.9, channel = "latest-only-test" });

        var (success, error) = await TryRunTransitionAsync(id, "stable-step");
        Assert.True(success, $"stable-step failed: {error}");
        await WaitForStateAsync(id, s => s == "completed");

        var instance = await Api.GetInstanceAsync(Workflow, id);

        // Start attributes (data version 1) and task output (later version) must both be
        // present in the merged latest view.
        Assert.Contains(orderId, instance.RawBody);
        Assert.Contains("checkResult", instance.RawBody);
    }

    [Fact]
    public async Task MultipleDataVersions_LatestViewStaysConsistent()
    {
        var orderId = $"ORD-{Guid.NewGuid():N}"[..12];
        var id = await StartInstanceAsync(new { orderId, amount = 1, channel = "versioned" });

        // Each transition appends data versions; ping-pong to accumulate history.
        foreach (var transition in new[] { "proceed", "back", "proceed", "finish" })
        {
            var (success, error) = await TryRunTransitionAsync(id, transition);
            Assert.True(success, $"{transition} failed: {error}");
        }

        var state = await WaitForStateAsync(id, s => s == "completed");
        Assert.Equal("completed", state);

        // The original start attributes must survive every version merge.
        var instance = await Api.GetInstanceAsync(Workflow, id);
        Assert.Contains(orderId, instance.RawBody);
    }

    [Fact]
    public async Task ListInstances_ReturnsStartedInstance()
    {
        var id = await StartInstanceAsync();

        var list = await Api.ListInstancesAsync(Workflow);
        Assert.True(list.Body.ValueKind != System.Text.Json.JsonValueKind.Null);
        Assert.Contains(id, list.RawBody);
    }
}
