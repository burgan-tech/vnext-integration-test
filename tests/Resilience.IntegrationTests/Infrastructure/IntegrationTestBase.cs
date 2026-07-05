using VNext.Testing.Sdk.Client;

namespace Resilience.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for all resilience integration tests.
/// Inherits shared fixtures from <see cref="VNext.Testing.Sdk.Infrastructure.IntegrationTestBase{TEnvironment}"/>
/// and adds helpers for tests that expect failures (transient faults, lock rejections).
/// </summary>
[Collection("VNextIntegration")]
public abstract class IntegrationTestBase
    : VNext.Testing.Sdk.Infrastructure.IntegrationTestBase<VNextTestEnvironment>
{
    protected const string Workflow = "resilience-check";

    protected IntegrationTestBase(VNextTestEnvironment environment) : base(environment) { }

    /// <summary>Creates a client pre-configured for the "resilience" domain.</summary>
    protected override VNextApiClient CreateApiClient(string baseUrl) =>
        new(new VNextApiClientOptions
        {
            BaseUrl = baseUrl,
            Domain = "resilience",
            ApiVersion = "1",
            TimeoutSeconds = 120
        });

    /// <summary>
    /// Starts a resilience-check instance synchronously and returns its id.
    /// </summary>
    protected async Task<string> StartInstanceAsync(object? attributes = null)
    {
        var payload = new
        {
            key = $"res-{Guid.NewGuid():N}",
            tags = new[] { "integration-test" },
            attributes = attributes ?? new
            {
                orderId = $"ORD-{Guid.NewGuid():N}"[..12],
                amount = 42.5,
                channel = "test"
            }
        };

        var response = await Api.StartInstanceAsync(Workflow, payload);
        var id = response.Body.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id), "StartInstance did not return an instance id.");
        return id!;
    }

    /// <summary>
    /// Runs a transition and swallows the HTTP failure the SDK client raises on
    /// non-success responses. Returns success flag plus the raw error for asserting.
    /// </summary>
    protected async Task<(bool Success, string? Error)> TryRunTransitionAsync(
        string instanceId, string transition, object? body = null)
    {
        try
        {
            await Api.RunTransitionAsync(Workflow, instanceId, transition, body);
            return (true, null);
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Retries a faulted instance and swallows failures, mirroring
    /// <see cref="TryRunTransitionAsync"/>.
    /// </summary>
    protected async Task<(bool Success, string? Error)> TryRetryInstanceAsync(string instanceId)
    {
        try
        {
            await Api.RetryInstanceAsync(Workflow, instanceId);
            return (true, null);
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Polls the instance until <paramref name="predicate"/> matches its current state
    /// or the timeout elapses. Returns the last observed state.
    /// </summary>
    protected async Task<string?> WaitForStateAsync(
        string instanceId, Func<string?, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        string? state = null;

        while (DateTime.UtcNow < deadline)
        {
            var instance = await Api.GetInstanceAsync(Workflow, instanceId);
            state = GetCurrentState(instance.Body);
            if (predicate(state))
                return state;

            await Task.Delay(1000);
        }

        return state;
    }

    /// <summary>
    /// Raw GET against the instance state function (long-polling endpoint).
    /// Uses GetRawAsync so 304 responses do not throw.
    /// </summary>
    protected Task<VNextApiResponse> GetStateFunctionAsync(
        string instanceId, Dictionary<string, string>? headers = null)
    {
        var path = $"/api/v1/resilience/workflows/{Workflow}/instances/{instanceId}/functions/state";
        return Api.GetRawAsync(path, headers);
    }
}

/// <summary>
/// xUnit collection definition — shares one <see cref="VNextTestEnvironment"/> across all
/// test classes in this project.
/// </summary>
[CollectionDefinition("VNextIntegration")]
public class VNextIntegrationCollection : ICollectionFixture<VNextTestEnvironment>
{
}
