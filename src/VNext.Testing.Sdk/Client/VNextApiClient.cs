using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VNext.Testing.Sdk.Client;

/// <summary>
/// Typed HTTP client for the vNext Runtime REST API.
/// Wraps workflow instance operations (start, transition, get, list) and function calls.
/// Override virtual methods to customize behaviour (e.g. add auth headers, retry logic).
/// </summary>
public class VNextApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly VNextApiClientOptions _options;

    protected static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <param name="baseUrl">Base URL of the vNext orchestrator (e.g. http://localhost:5000).</param>
    public VNextApiClient(string baseUrl)
        : this(new VNextApiClientOptions { BaseUrl = baseUrl })
    {
    }

    /// <param name="options">Full configuration options for the client.</param>
    public VNextApiClient(VNextApiClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _http = CreateHttpClient();
        ConfigureHttpClient(_http);
    }

    // ========================================================================
    // Extension points
    // ========================================================================

    /// <summary>
    /// Factory hook — override to supply a pre-configured HttpClient (e.g. from IHttpClientFactory).
    /// </summary>
    protected virtual HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    /// <summary>
    /// Hook called after the HttpClient is created. Override to add default headers, handlers, etc.
    /// </summary>
    protected virtual void ConfigureHttpClient(HttpClient http)
    {
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", _options.AcceptLanguage);

        foreach (var (key, value) in _options.AdditionalHeaders)
            http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
    }

    /// <summary>
    /// Hook called just before every HTTP request is sent. Override to inject per-request headers (e.g. correlation ID).
    /// </summary>
    protected virtual Task OnBeforeRequestAsync(HttpRequestMessage request) => Task.CompletedTask;

    /// <summary>
    /// Hook called after a non-success HTTP response. Override to implement retry, circuit-breaker, etc.
    /// Default behaviour: throw HttpRequestException.
    /// </summary>
    protected virtual Task OnNonSuccessResponseAsync(
        string operation, System.Net.HttpStatusCode statusCode, string body)
    {
        throw new HttpRequestException($"{operation} failed ({statusCode}): {body}");
    }

    // ========================================================================
    // Workflow Instance Operations
    // ========================================================================

    /// <summary>
    /// Start a new workflow instance.
    /// POST /api/v{version}/{domain}/workflows/{workflow}/instances/start?sync=true
    /// </summary>
    public virtual async Task<JsonElement> StartInstanceAsync(string workflow, object body)
    {
        var url = BuildUrl($"workflows/{workflow}/instances/start", sync: true);
        return await SendJsonAsync(HttpMethod.Post, url, body, "StartInstance");
    }

    /// <summary>
    /// Run a transition on an existing workflow instance.
    /// PATCH /api/v{version}/{domain}/workflows/{workflow}/instances/{id}/transitions/{transition}?sync=true
    /// </summary>
    public virtual async Task<JsonElement> RunTransitionAsync(
        string workflow, string instanceId, string transition, object? body = null)
    {
        var url = BuildUrl($"workflows/{workflow}/instances/{instanceId}/transitions/{transition}", sync: true);
        var payload = body ?? new { attributes = new { } };
        return await SendJsonAsync(HttpMethod.Patch, url, payload, "RunTransition");
    }

    /// <summary>
    /// Get a single workflow instance by ID.
    /// GET /api/v{version}/{domain}/workflows/{workflow}/instances/{id}
    /// </summary>
    public virtual async Task<JsonElement> GetInstanceAsync(string workflow, string instanceId)
    {
        var url = BuildUrl($"workflows/{workflow}/instances/{instanceId}");
        return await GetJsonAsync(url, "GetInstance");
    }

    /// <summary>
    /// List workflow instances with optional query parameters.
    /// GET /api/v{version}/{domain}/workflows/{workflow}/instances
    /// </summary>
    public virtual async Task<JsonElement> ListInstancesAsync(
        string workflow, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl($"workflows/{workflow}/instances", queryParams: queryParams);
        return await GetJsonAsync(url, "ListInstances");
    }

    // ========================================================================
    // Function Operations
    // ========================================================================

    /// <summary>
    /// Call a domain-level function (scope I).
    /// GET /api/v{version}/{domain}/functions/{functionName}
    /// </summary>
    public virtual async Task<JsonElement> CallFunctionAsync(
        string functionName, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl($"functions/{functionName}", queryParams: queryParams);
        return await GetJsonAsync(url, "CallFunction");
    }

    /// <summary>
    /// Call a workflow-level function (scope F).
    /// GET /api/v{version}/{domain}/workflows/{workflow}/functions/{functionName}
    /// </summary>
    public virtual async Task<JsonElement> CallWorkflowFunctionAsync(
        string workflow, string functionName, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl($"workflows/{workflow}/functions/{functionName}", queryParams: queryParams);
        return await GetJsonAsync(url, "CallWorkflowFunction");
    }

     /// <summary>
    /// Call a workflow instance-level function (scope I).
    /// GET /api/v{version}/{domain}/workflows/{workflow}/instances/{instanceId}/functions/{functionName}
    /// </summary>
    public virtual async Task<JsonElement> CallInstanceFunctionAsync(
        string workflow, string instanceId, string functionName, Dictionary<string, string>? queryParams = null)
    {
        var url = BuildUrl($"workflows/{workflow}/instances/{instanceId}/functions/{functionName}", queryParams: queryParams);
        return await GetJsonAsync(url, "CallInstanceFunction");
    }

    // ========================================================================
    // Health & Utility
    // ========================================================================

    /// <summary>
    /// Wait until the vNext API health endpoint returns 200.
    /// </summary>
    public virtual async Task WaitForHealthyAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromMinutes(2);
        var deadline = DateTime.UtcNow + timeout.Value;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await _http.GetAsync("/health");
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("[VNextApiClient] API is healthy");
                    return;
                }
            }
            catch { /* not ready yet */ }

            await Task.Delay(2000);
        }

        throw new TimeoutException($"vNext API did not become healthy within {timeout.Value}");
    }

    /// <summary>
    /// Raw GET request to any path — useful for debugging or custom endpoints.
    /// </summary>
    public virtual async Task<(int StatusCode, string Body)> GetRawAsync(string path)
    {
        var response = await _http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body);
    }

    // ========================================================================
    // Internal helpers
    // ========================================================================

    private string BuildUrl(
        string path,
        bool sync = false,
        Dictionary<string, string>? queryParams = null)
    {
        var url = $"/api/v{_options.ApiVersion}/{_options.Domain}/{path}";

        var allParams = new Dictionary<string, string>();
        if (sync) allParams["sync"] = "true";
        if (queryParams != null)
            foreach (var kv in queryParams) allParams[kv.Key] = kv.Value;

        if (allParams.Count > 0)
        {
            var qs = string.Join("&", allParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            url += $"?{qs}";
        }

        return url;
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method, string url, object body, string operation)
    {
        var json = JsonSerializer.Serialize(body, DefaultJsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(method, url) { Content = content };
        await OnBeforeRequestAsync(request);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            await OnNonSuccessResponseAsync(operation, response.StatusCode, responseBody);
            // If overrider didn't throw, return empty element
            return default;
        }

        return JsonSerializer.Deserialize<JsonElement>(responseBody, DefaultJsonOptions);
    }

    private async Task<JsonElement> GetJsonAsync(string url, string operation)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        await OnBeforeRequestAsync(request);

        var response = await _http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            await OnNonSuccessResponseAsync(operation, response.StatusCode, responseBody);
            return default;
        }

        return JsonSerializer.Deserialize<JsonElement>(responseBody, DefaultJsonOptions);
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}
