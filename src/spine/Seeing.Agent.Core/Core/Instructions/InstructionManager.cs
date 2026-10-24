using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Context;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令管理器：<b>薄适配器</b>。发现委托 <see cref="InstructionDiscovery"/>，
/// 注入委托 <see cref="IContextAssembler"/>（Persist 模式），再把装配结果投影为
/// <see cref="InstructionInjectResult"/>。
/// <para>真相源唯一：指令内容、变更判定与注入位置全部由上下文管道负责；
/// 本类不再维护指纹双轨（<see cref="InstructionFingerprintStore"/> 仅为兼容
/// <see cref="GetFingerprints"/> 保留，Task 13 清理）。</para>
/// </summary>
public sealed class InstructionManager : IInstructionManager
{
    private static readonly Regex s_filePathRegex = new(
        "<file path=\"(?<path>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IContextAssembler _assembler;
    private readonly InstructionDiscovery _discovery;
    private readonly ILogger<InstructionManager> _logger;

    /// <summary>
    /// 构造指令管理器。
    /// </summary>
    /// <param name="assembler">上下文装配器（唯一注入出口）。</param>
    /// <param name="logger">管理器日志。</param>
    /// <param name="loggerFactory">发现器日志工厂。</param>
    public InstructionManager(
        IContextAssembler assembler,
        ILogger<InstructionManager> logger,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _assembler = assembler;
        _logger = logger;
        _discovery = new InstructionDiscovery(loggerFactory.CreateLogger<InstructionDiscovery>());
    }

    /// <summary>
    /// 委托发现器从 cwd 向上扫描至 workspaceRoot 的指令文件。
    /// </summary>
    public Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default) =>
        _discovery.DiscoverAsync(cwd, workspaceRoot, ct);

    /// <summary>
    /// 触发一次 Persist 装配，并把结果投影为注入结果。
    /// <para><paramref name="cwd"/> / <paramref name="workspaceRoot"/> 为旧签名遗留参数：
    /// 迁移后根由 <c>ProjectInstructionsContextSource</c> 自行解析（装配器不传外部上下文），
    /// 仅在此做偏离记录，避免静默不一致。</para>
    /// </summary>
    public async Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default,
        string? insertBeforeMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!string.IsNullOrWhiteSpace(cwd) || !string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "指令注入根由源自行解析，忽略调用方参数：会话={SessionId} Cwd={Cwd} WorkspaceRoot={WorkspaceRoot}",
                session.Id,
                cwd,
                workspaceRoot);
        }

        var result = await _assembler.EnsureFreshAsync(
            session,
            ContextAssemblyMode.Persist,
            insertBeforeMessageId,
            skip: null,
            ct).ConfigureAwait(false);

        var initial = !string.IsNullOrWhiteSpace(result.HistoryBaseline);
        var delta = !string.IsNullOrWhiteSpace(result.DeltaText);

        if (!initial && !delta)
        {
            return new InstructionInjectResult
            {
                Injected = false,
                Reason = ProjectInstructions.Reasons.None
            };
        }

        // 原因判定只依赖装配结果：初始渲染 → initial；增量（内容新增/变更/删除）→ content-change。
        // cwd-change 不再参与判定：cwd 由源解析，管道不暴露「根是否变化」这一维度。
        var reason = initial
            ? ProjectInstructions.Reasons.Initial
            : ProjectInstructions.Reasons.ContentChange;

        var paths = ExtractFilePaths(result.HistoryBaseline)
            .Concat(ExtractFilePaths(result.DeltaText))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _logger.LogDebug(
            "会话 {SessionId} 项目指令已注入：原因={Reason} 文件数={Count}",
            session.Id,
            reason,
            paths.Length);

        return new InstructionInjectResult
        {
            Injected = true,
            Reason = reason,
            InjectedPaths = paths
        };
    }

    /// <summary>
    /// 读取会话当前的指令文件指纹快照。
    /// <para>兼容保留：迁移后不再写入指纹，返回值恒为空快照，Task 13 随 <see cref="IInstructionManager"/> 一并清理。</para>
    /// </summary>
    public InstructionFingerprintSnapshot GetFingerprints(SessionData session) =>
        InstructionFingerprintStore.Load(session);

    /// <summary>
    /// 从渲染文本的 <c>&lt;file path="…"&gt;</c> 块中提取路径（管道只暴露渲染结果）。
    /// </summary>
    private static IEnumerable<string> ExtractFilePaths(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (Match match in s_filePathRegex.Matches(text))
        {
            if (!match.Success)
                continue;

            yield return WebUtility.HtmlDecode(match.Groups["path"].Value);
        }
    }
}
