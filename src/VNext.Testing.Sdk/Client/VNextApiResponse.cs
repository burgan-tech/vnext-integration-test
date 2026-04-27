using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VNext.Testing.Sdk.Client;

/// <summary>
/// Wraps an HTTP response with status code, headers, raw body and deserialized JSON body.
/// Returned by all <see cref="VNextApiClient"/> API methods so callers can inspect
/// response metadata alongside the payload.
/// </summary>
public sealed class VNextApiResponse
{
    public HttpStatusCode StatusCode { get; init; }
    public HttpResponseHeaders Headers { get; init; } = null!;
    public JsonElement Body { get; init; }
    public string RawBody { get; init; } = string.Empty;

    public bool IsSuccessStatusCode => (int)StatusCode >= 200 && (int)StatusCode < 300;
}
