using Microsoft.Extensions.Logging;
using Seeing.Agent.Reflection.Abstractions;

namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 空实现执行器：仅记录调试日志，不做任何实际复盘。
/// <para>
/// 真实实现（<see cref="ReflectionExecutor"/>）已就位，本类型不再参与 DI 注册，
/// 仅保留给需要显式替换执行器的宿主或测试使用。
/// </para>
/// </summary>
public sealed class NullReflectionExecutor : IReflectionExecutor
{
    private readonly ILogger<NullReflectionExecutor>? _logger;

    /// <summary>创建空实现执行器。</summary>
    public NullReflectionExecutor(ILogger<NullReflectionExecutor>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public Task<ReflectionRunResult> RunAsync(ReflectionRequest request, CancellationToken ct = default)
    {
        _logger?.LogDebug(
            "复盘执行器为空实现，跳过本次复盘：SessionId={SessionId}, Kind={Kind}",
            request.SessionId,
            request.Kind);
        return Task.FromResult(ReflectionRunResult.Skipped(ReflectionSkipReasons.Stub));
    }
}
