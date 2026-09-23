using Seeing.Agent.SystemOne.Configuration;

namespace Seeing.Agent.SystemOne.Tests;

/// <summary>临时设置环境变量并在 Dispose 时恢复原值（含删除）。</summary>
internal sealed class EnvVarScope : IDisposable
{
    private readonly List<(string Name, string? Original)> _saved = new();

    public EnvVarScope(params (string Name, string? Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            _saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, original) in _saved)
            Environment.SetEnvironmentVariable(name, original);
        _saved.Clear();
    }
}

/// <summary>一次性设置四个 SYSTEMONE_* 变量（未传的显式清空，避免测试间污染）。</summary>
internal static class SystemOneTestEnv
{
    public static EnvVarScope Scope(
        string? apiKey = null,
        string? baseUrl = null,
        string? model = null,
        string? provider = null)
        => new(
            (SystemOneEnvironmentConfig.ApiKeyVar, apiKey),
            (SystemOneEnvironmentConfig.BaseUrlVar, baseUrl),
            (SystemOneEnvironmentConfig.ModelVar, model),
            (SystemOneEnvironmentConfig.ProviderVar, provider));
}
