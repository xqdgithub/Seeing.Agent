using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Models;

namespace Seeing.Gateway.Client;

/// <summary>
/// 基于 HTTP/SSE 的 <see cref="IGatewayClient"/> 实现
/// </summary>
public sealed class HttpGatewayClient : IGatewayClient
{
    private readonly HttpClient _httpClient;

    public HttpGatewayClient(HttpClient httpClient, IOptions<GatewayClientOptions> options)
    {
        _httpClient = httpClient;
        ApplyOptions(httpClient, options.Value);
    }

    public async Task<GatewaySubmitResult> SubmitAsync(
        GatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/gateway/submit",
            request,
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        var result = await response.Content.ReadFromJsonAsync<GatewaySubmitResult>(
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        return result ?? GatewaySubmitResult.Failed(request.SessionId, "Empty submit response");
    }

    public async IAsyncEnumerable<GatewayEvent> SubscribeAsync(
        string sessionId,
        string executionId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var query = $"sessionId={Uri.EscapeDataString(sessionId)}&executionId={Uri.EscapeDataString(executionId)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"api/gateway/events?{query}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var gatewayEvent in SseEventReader.ReadEventsAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            yield return gatewayEvent;
        }
    }

    public async Task CancelAsync(string executionId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "api/gateway/cancel",
            new { executionId },
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    public async Task<GatewayPermissionRespondResult> RespondPermissionAsync(
        string sessionId,
        string permissionId,
        bool allow,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var encodedPermissionId = Uri.EscapeDataString(permissionId);
        var body = new
        {
            sessionId,
            allow,
            reason
        };

        using var response = await _httpClient.PostAsJsonAsync(
            $"api/gateway/permissions/{encodedPermissionId}/respond",
            body,
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return GatewayPermissionRespondResult.Fail(
                string.IsNullOrWhiteSpace(error) ? response.ReasonPhrase ?? "Request failed" : error);
        }

        var result = await response.Content.ReadFromJsonAsync<GatewayPermissionRespondResult>(
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        return result ?? GatewayPermissionRespondResult.Fail("Empty response body");
    }

    public async Task<IReadOnlyList<GatewayPendingPermission>> GetPendingPermissionsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var encodedSessionId = Uri.EscapeDataString(sessionId);
        using var response = await _httpClient.GetAsync(
            $"api/gateway/permissions/pending?sessionId={encodedSessionId}",
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var permissions = await response.Content.ReadFromJsonAsync<List<GatewayPendingPermission>>(
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        return permissions ?? [];
    }

    public async Task<GatewaySessionResetResult> ResetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var encodedSessionId = Uri.EscapeDataString(sessionId);
        using var response = await _httpClient.PostAsync(
            $"api/gateway/sessions/{encodedSessionId}/reset",
            content: null,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Session not found: {sessionId}");

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GatewaySessionResetResult>(
            GatewayJsonOptions.Default,
            cancellationToken).ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("Empty reset response body");
    }

    private static void ApplyOptions(HttpClient httpClient, GatewayClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("Gateway BaseUrl is required.");
        }

        httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        httpClient.Timeout = options.Timeout;

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Remove("X-Api-Key");
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        }
    }
}
