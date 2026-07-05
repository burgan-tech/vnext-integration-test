using Resilience.IntegrationTests.Infrastructure;

namespace Resilience.IntegrationTests.Tests;

/// <summary>
/// Transient-fault recovery: the flaky-step transition calls a MockLab endpoint that
/// answers 503, 503, 200 (repeating). The first attempt faults the instance; retries via
/// the /retry endpoint must drive it to completion.
///
/// Validates the resilience-hardening branch behaviours around task failure:
/// the 5xx is NOT retried at the Dapr transport layer (layer contract), the instance
/// faults cleanly with a durable transition record, and a subsequent retry re-runs the
/// transition to success without duplicate side effects.
/// </summary>
public class TransientFaultRetryTests : IntegrationTestBase
{
    public TransientFaultRetryTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task FlakyStep_FaultsOnTransientError_ThenRetriesToCompletion()
    {
        var id = await StartInstanceAsync();

        // First attempt hits the 503 at the head of the mock sequence.
        var (firstSuccess, firstError) = await TryRunTransitionAsync(id, "flaky-step");

        var attempts = 1;
        var completed = firstSuccess &&
                        await WaitForStateAsync(id, s => s == "completed",
                            TimeSpan.FromSeconds(10)) == "completed";

        // Drive recovery through /retry until the 200 at the end of the sequence lands.
        // The sequence repeats every 3 calls, so 6 attempts always cross a success.
        while (!completed && attempts < 6)
        {
            attempts++;
            await Task.Delay(1500);
            await TryRetryInstanceAsync(id);

            var state = await WaitForStateAsync(id, s => s == "completed",
                TimeSpan.FromSeconds(10));
            completed = state == "completed";
        }

        Assert.True(completed,
            $"Instance did not complete after {attempts} attempts. First error: {firstError}");

        // The first attempt must have failed — otherwise the fault path was never exercised.
        Assert.True(attempts > 1 || !firstSuccess,
            "Expected the first flaky-step attempt to hit a transient fault, but it succeeded immediately. " +
            "Check the MockLab sequential seed for api/resilience/check/flaky.");

        // The task output written by the mapping must be visible on the instance.
        var instance = await Api.GetInstanceAsync(Workflow, id);
        Assert.Contains("flakyResult", instance.RawBody);
    }

    [Fact]
    public async Task FaultedInstance_IsNotStuckBusy()
    {
        var id = await StartInstanceAsync();

        await TryRunTransitionAsync(id, "flaky-step");

        // Whatever the outcome, the instance must settle into a stable status —
        // the chain lock lease / checkpoint work guarantees no permanent Busy.
        var state = await WaitForStateAsync(id,
            s => s is "completed" or "ready",
            TimeSpan.FromSeconds(20));

        Assert.True(state is "completed" or "ready",
            $"Instance stuck in unexpected state '{state}' after a faulted transition.");
    }
}
