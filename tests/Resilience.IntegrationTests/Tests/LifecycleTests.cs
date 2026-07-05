using Resilience.IntegrationTests.Infrastructure;

namespace Resilience.IntegrationTests.Tests;

/// <summary>
/// Happy-path lifecycle: start → ready → stable-step (HTTP task via Execution + Dapr) → completed.
/// Exercises the full transition pipeline including the task invocation path that the
/// Dapr resiliency spec and invocation-timeout budget hierarchy sit on.
/// </summary>
public class LifecycleTests : IntegrationTestBase
{
    public LifecycleTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task StartInstance_LandsOnReadyState()
    {
        var id = await StartInstanceAsync();

        var instance = await Api.GetInstanceAsync(Workflow, id);
        Assert.Equal("ready", GetCurrentState(instance.Body));
    }

    [Fact]
    public async Task StableStep_RunsHttpTask_AndCompletesInstance()
    {
        var id = await StartInstanceAsync();

        var (success, error) = await TryRunTransitionAsync(id, "stable-step");
        Assert.True(success, $"stable-step transition failed: {error}");

        var state = await WaitForStateAsync(id, s => s == "completed");
        Assert.Equal("completed", state);
    }

    [Fact]
    public async Task ManualChain_ProceedBackProceedFinish_CompletesInstance()
    {
        var id = await StartInstanceAsync();

        var (s1, e1) = await TryRunTransitionAsync(id, "proceed");
        Assert.True(s1, $"proceed failed: {e1}");

        var (s2, e2) = await TryRunTransitionAsync(id, "back");
        Assert.True(s2, $"back failed: {e2}");

        var (s3, e3) = await TryRunTransitionAsync(id, "proceed");
        Assert.True(s3, $"second proceed failed: {e3}");

        var (s4, e4) = await TryRunTransitionAsync(id, "finish");
        Assert.True(s4, $"finish failed: {e4}");

        var state = await WaitForStateAsync(id, s => s == "completed");
        Assert.Equal("completed", state);
    }
}
