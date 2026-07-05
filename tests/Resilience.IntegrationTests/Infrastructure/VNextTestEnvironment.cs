namespace Resilience.IntegrationTests.Infrastructure;

/// <summary>
/// Test environment for the resilience-hardening validation suite.
///
/// Pins the vNext images to a branch build of <c>feature/resilience-hardening</c>:
/// set <c>VNEXT_IMAGE_VERSION</c> (e.g. <c>0.0.69-alpha.1</c> from a CI prerelease dispatch)
/// or build the images locally with <c>scripts/build-vnext-branch-images.sh</c>, which tags
/// them as <c>resilience-local</c> — the default used here.
///
/// The new resilience features (Postgres lock lease store, lease extension, latest-only
/// instance loading) are enabled explicitly via the <c>WorkflowExecution</c> section in
/// <c>Config/appsettings.orchestration.json</c>, and the Dapr resiliency spec lives in
/// <c>Infrastructure/DaprComponents/orchestration/resiliency.yaml</c> (its target app-id
/// matches this environment's execution sidecar: <c>vnext-execution-app-resilience</c>).
/// </summary>
public class VNextTestEnvironment : VNext.Testing.Sdk.Infrastructure.VNextTestEnvironment
{
    /// <summary>APP_DOMAIN value passed to vNext containers.</summary>
    protected override string Domain => "resilience";

    /// <summary>PostgreSQL database name created during test setup.</summary>
    protected override string DatabaseName => "vNext_Resilience_Test";

    /// <summary>
    /// Image tag for the orchestrator / execution / db-migrator containers.
    /// Resolution: VNEXT_IMAGE_VERSION environment variable, else "resilience-local"
    /// (the tag produced by scripts/build-vnext-branch-images.sh).
    /// </summary>
    protected override string VNextImageVersion =>
        System.Environment.GetEnvironmentVariable("VNEXT_IMAGE_VERSION") ?? "resilience-local";
}
