using System.Net.Http.Json;
using System.Text.Json;

namespace Seeing.Agent.Cli.Services;

public sealed class ManagementApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ManagementApiClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public ManagementApiClient(string baseUrl, HttpMessageHandler handler)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<AdminStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{_baseUrl}/api/admin/status", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AdminStatusResponse>(cancellationToken: ct);
    }

    public async Task<bool> ShutdownAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"{_baseUrl}/api/admin/shutdown", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/gateway/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class AdminStatusResponse
{
    public string Status { get; set; } = string.Empty;
    public string Uptime { get; set; } = string.Empty;
    public double UptimeSeconds { get; set; }
    public int GatewayPort { get; set; }
    public int ActiveSessions { get; set; }
    public int ActiveExecutions { get; set; }
    public bool SchedulerRunning { get; set; }
}
