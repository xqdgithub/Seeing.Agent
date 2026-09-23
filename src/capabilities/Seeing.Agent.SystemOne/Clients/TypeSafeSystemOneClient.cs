using Seeing.Agent.Abstractions.SystemOne;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.SystemOne.Clients;

/// <summary>TypeSafe（Jev）System One 协议客户端。</summary>
public sealed class TypeSafeSystemOneClient : ISystemOneClient, IDisposable
{
    private readonly SystemOneProviderConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<TypeSafeSystemOneClient> _logger;

    /// <summary>构造 TypeSafe 客户端；HttpClient 由本类负责释放。</summary>
    public TypeSafeSystemOneClient(
        SystemOneProviderConfig config,
        HttpClient http,
        ILogger<TypeSafeSystemOneClient> logger)
    {
        _config = config;
        _http = http;
        _logger = logger;
    }

    /// <summary>provider id。</summary>
    public string ProviderId => _config.Id;

    /// <summary>provider 类型（固定 typesafe）。</summary>
    public string ProviderType => SystemOneProviderTypes.TypeSafe;

    /// <summary>发送 typed questions 并返回结构化应答。</summary>
    public Task<SystemOneResponse> EvaluateAsync(SystemOneRequest request, CancellationToken ct)
    {
        // 模型补全：request.Model 为空时用 provider 配置的 Model（使 SYSTEMONE_MODEL 生效）
        var effective = string.IsNullOrWhiteSpace(request.Model)
            ? request with { Model = _config.Model }
            : request;

        _logger.LogDebug("SystemOne 评估: provider={ProviderId}, model={Model}", _config.Id, effective.Model);

        return SystemOneHttpHelper.PostAsync<SystemOneRequest, SystemOneResponse>(
            _http, _config, SystemOneDefaults.EvaluatePath, effective, ct);
    }

    /// <summary>列出可用模型/别名。</summary>
    public async Task<IReadOnlyList<SystemOneModel>> ListModelsAsync(CancellationToken ct)
    {
        var resp = await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
            _http, _config, SystemOneDefaults.ModelsPath, ct).ConfigureAwait(false);
        return resp.Models;
    }

    /// <summary>连通性探测：能列出模型即视为连通；异常吞掉返回 false。</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await SystemOneHttpHelper.GetAsync<SystemOneModelsResponse>(
                _http, _config, SystemOneDefaults.ModelsPath, ct).ConfigureAwait(false);
            return true;
        }
        catch (SystemOneException) { return false; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>释放底层 HttpClient。</summary>
    public void Dispose() => _http.Dispose();
}
