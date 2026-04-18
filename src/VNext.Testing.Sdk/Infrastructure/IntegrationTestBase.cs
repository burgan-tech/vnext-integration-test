using System.Text.Json;
using VNext.Testing.Sdk.Client;

namespace VNext.Testing.Sdk.Infrastructure;

/// <summary>
/// Base class for all vNext integration tests.
/// Provides the shared <see cref="VNextTestEnvironment"/> fixture and a pre-configured
/// <see cref="VNextApiClient"/> ready for use in test methods.
///
/// Usage:
///   1. Subclass <see cref="VNextTestEnvironment"/> if you need to override infrastructure.
///   2. Declare a collection using <see cref="VNextCollectionDefinitionAttribute{TEnvironment}"/>.
///   3. Inherit your test class from <see cref="IntegrationTestBase{TEnvironment}"/> and decorate
///      it with the matching <c>[Collection]</c> attribute.
/// </summary>
/// <typeparam name="TEnvironment">
/// Concrete <see cref="VNextTestEnvironment"/> used by this test suite.
/// Defaults to <see cref="VNextTestEnvironment"/> when not specified.
/// </typeparam>
public abstract class IntegrationTestBase<TEnvironment> : IDisposable
    where TEnvironment : VNextTestEnvironment
{
    protected readonly TEnvironment Environment;
    protected readonly VNextApiClient Api;

    protected IntegrationTestBase(TEnvironment environment)
    {
        Environment = environment;
        Api = CreateApiClient(environment.OrchestratorBaseUrl);
    }

    /// <summary>
    /// Factory for the <see cref="VNextApiClient"/>. Override to supply a customised client.
    /// </summary>
    protected virtual VNextApiClient CreateApiClient(string baseUrl) =>
        new(baseUrl);

    /// <summary>
    /// Extracts <c>currentState</c> from a vNext instance response.
    /// Handles both flat (<c>currentState</c>) and nested (<c>metadata.currentState</c>) formats.
    /// </summary>
    protected static string? GetCurrentState(JsonElement instance)
    {
        if (instance.TryGetProperty("currentState", out var direct))
            return direct.GetString();
        if (instance.TryGetProperty("metadata", out var meta) &&
            meta.TryGetProperty("currentState", out var nested))
            return nested.GetString();
        return null;
    }

    public void Dispose()
    {
        Api.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Convenience base class using the default <see cref="VNextTestEnvironment"/>.
/// </summary>
public abstract class IntegrationTestBase : IntegrationTestBase<VNextTestEnvironment>
{
    protected IntegrationTestBase(VNextTestEnvironment environment) : base(environment) { }
}
