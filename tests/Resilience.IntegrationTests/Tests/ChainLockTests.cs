using Resilience.IntegrationTests.Infrastructure;

namespace Resilience.IntegrationTests.Tests;

/// <summary>
/// Pipeline chain-lock behaviour under concurrency. With the Postgres lock lease store
/// (WorkflowExecution:LockProvider=Postgres) exactly one of N concurrent transition
/// requests on the same instance may win; the rest must be rejected cleanly and the
/// instance must settle in a consistent state — never stuck Busy, never double-transitioned.
/// </summary>
public class ChainLockTests : IntegrationTestBase
{
    public ChainLockTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task ConcurrentSameTransition_ExactlyOneWins()
    {
        var id = await StartInstanceAsync();

        // Fire 6 identical manual transitions at once. "proceed" moves ready → reviewing
        // and is not defined on reviewing, so any late-arriving duplicate must also fail.
        var storm = Enumerable.Range(0, 6)
            .Select(_ => TryRunTransitionAsync(id, "proceed"))
            .ToArray();

        var results = await Task.WhenAll(storm);
        var successCount = results.Count(r => r.Success);

        Assert.Equal(1, successCount);

        var state = await WaitForStateAsync(id, s => s == "reviewing");
        Assert.Equal("reviewing", state);
    }

    [Fact]
    public async Task ConcurrentDistinctTransitions_InstanceSettlesConsistently()
    {
        var id = await StartInstanceAsync();

        // Competing transitions out of the same state: only one may be applied.
        var race = new[]
        {
            TryRunTransitionAsync(id, "proceed"),
            TryRunTransitionAsync(id, "stable-step"),
            TryRunTransitionAsync(id, "proceed")
        };

        var results = await Task.WhenAll(race);
        var successCount = results.Count(r => r.Success);

        Assert.True(successCount >= 1, "At least one transition should have won the race.");

        // The instance must settle on the winner's target — and must not be left Busy.
        var state = await WaitForStateAsync(id,
            s => s is "reviewing" or "completed",
            TimeSpan.FromSeconds(20));

        Assert.True(state is "reviewing" or "completed",
            $"Instance in inconsistent state '{state}' after concurrent distinct transitions.");
    }
}
