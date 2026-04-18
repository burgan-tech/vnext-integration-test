namespace VNext.Testing.Sdk.Client;

/// <summary>
/// Configuration options for <see cref="VNextApiClient"/>.
/// All properties have sensible defaults so only <see cref="BaseUrl"/> is mandatory.
/// </summary>
public sealed class VNextApiClientOptions
{
    /// <summary>Base URL of the vNext orchestrator (e.g. http://localhost:5000).</summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>Domain name used in REST path segments (e.g. "touch", "fx", "core").</summary>
    public string Domain { get; set; } = "touch";

    /// <summary>vNext API version number used in the URL path (e.g. "1").</summary>
    public string ApiVersion { get; set; } = "1";

    /// <summary>HTTP client timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Accept-Language header value sent with every request.</summary>
    public string AcceptLanguage { get; set; } = "tr-TR";

    /// <summary>
    /// Additional HTTP headers injected into every request via
    /// <see cref="VNextApiClient.ConfigureHttpClient"/>.
    /// </summary>
    public Dictionary<string, string> AdditionalHeaders { get; set; } = new();
}
