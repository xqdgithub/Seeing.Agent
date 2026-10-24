using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Instructions;

namespace Seeing.Agent.Core.Context.Sources;

/// <summary>
/// 指令文件读取端口实现：复用 <see cref="InstructionDiscovery.EnumerateCandidatePaths"/> 的搜索根规则，
/// 逐路径读取并分类为 <see cref="InstructionFileReadStatus.Loaded"/> /
/// <see cref="InstructionFileReadStatus.Missing"/> / <see cref="InstructionFileReadStatus.Unreadable"/>。
/// <para>不做存在性预判（<c>File.Exists</c> 在无权限时同样返回 false，会把「不可读」误判为「缺失」），
/// 直接尝试读取，按异常类型分类。</para>
/// </summary>
public sealed class InstructionFileReader : IInstructionFileReader
{
    private readonly InstructionDiscovery _discovery;
    private readonly ILogger<InstructionFileReader> _logger;

    /// <summary>
    /// 创建逐路径读取器。
    /// </summary>
    /// <param name="discovery">候选路径发现器（搜索根规则的唯一实现，由 DI 单例注入，不在本类内 new）。</param>
    /// <param name="logger">读取告警日志。</param>
    internal InstructionFileReader(
        InstructionDiscovery discovery,
        ILogger<InstructionFileReader> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _discovery = discovery;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstructionFileReadResult>> ReadAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        var candidates = _discovery.EnumerateCandidatePaths(cwd, workspaceRoot);
        var results = new List<InstructionFileReadResult>(candidates.Count);

        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ReadOneAsync(path, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// 读取单个路径：缺失 → <see cref="InstructionFileReadStatus.Missing"/>；
    /// IO / 权限等失败 → <see cref="InstructionFileReadStatus.Unreadable"/>（记录告警，绝不抛出）。
    /// </summary>
    private async Task<InstructionFileReadResult> ReadOneAsync(string path, CancellationToken ct)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return new InstructionFileReadResult(path, InstructionFileReadStatus.Loaded, content, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return new InstructionFileReadResult(path, InstructionFileReadStatus.Missing, null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new InstructionFileReadResult(path, InstructionFileReadStatus.Missing, null, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unreadable(path, ex);
        }
        catch (IOException ex)
        {
            return Unreadable(path, ex);
        }
        catch (Exception ex)
        {
            // 端口契约要求「永不抛出、永不返回 null」：其余异常同样降级为不可读并记录
            return Unreadable(path, ex);
        }
    }

    private InstructionFileReadResult Unreadable(string path, Exception ex)
    {
        var error = $"{ex.GetType().Name}: {ex.Message}";
        _logger.LogWarning(
            ex,
            "读取指令文件失败，按不可读处理（保留上次已叙述内容）：{Path} {Status}",
            path,
            InstructionFileReadStatus.Unreadable);

        return new InstructionFileReadResult(path, InstructionFileReadStatus.Unreadable, null, error);
    }
}
