using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Core.Context.Sources;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令管理器：<b>薄适配器</b>。
/// <para>真相源唯一：指令内容、变更判定与注入位置全部由上下文管道负责；
/// 本类只做两件事——把调用方的执行根交给源（<see cref="IInstructionRootsProvider"/>），
/// 再把装配结果投影为 <see cref="InstructionInjectResult"/>。</para>
/// <para>本类不再维护指纹双轨，也不再从渲染文本反解路径（同一事实只有一条产出路径）。</para>
/// </summary>
public sealed class InstructionManager : IInstructionManager
{
    private readonly IContextAssembler _assembler;
    private readonly IInstructionRootsProvider _rootsProvider;
    private readonly ILogger<InstructionManager> _logger;

    /// <summary>
    /// 构造指令管理器。
    /// </summary>
    /// <param name="assembler">上下文装配器（唯一注入出口）。</param>
    /// <param name="rootsProvider">执行作用域扫描根端口（把调用方 cwd 真正交给源）。</param>
    /// <param name="logger">管理器日志。</param>
    public InstructionManager(
        IContextAssembler assembler,
        IInstructionRootsProvider rootsProvider,
        ILogger<InstructionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        ArgumentNullException.ThrowIfNull(rootsProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _assembler = assembler;
        _rootsProvider = rootsProvider;
        _logger = logger;
    }

    /// <summary>
    /// 触发一次 Persist 装配，并把结果投影为注入结果。
    /// <para><paramref name="cwd"/> / <paramref name="workspaceRoot"/> 在本次装配的异步流内覆盖扫描根，
    /// 因此不会再出现「签名在、值被丢弃」（源侧禁止读进程当前目录）。</para>
    /// </summary>
    public async Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default,
        string? insertBeforeMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var rootsScope = _rootsProvider.Push(new InstructionRoots(cwd, workspaceRoot));

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
        var reason = initial
            ? ProjectInstructions.Reasons.Initial
            : ProjectInstructions.Reasons.ContentChange;

        _logger.LogDebug(
            "会话 {SessionId} 项目指令已注入：原因={Reason} 变化源数={ChangedCount} 问题数={IssueCount}",
            session.Id,
            reason,
            result.ChangedSourceKeys.Count,
            result.Issues.Count);

        return new InstructionInjectResult
        {
            Injected = true,
            Reason = reason
        };
    }

    /// <summary>
    /// 读取会话当前的指令文件指纹快照。
    /// <para>兼容保留：迁移后不再写入指纹，返回值恒为空快照（不得返回 store 里的陈旧数据）。
    /// Task 13 随 <see cref="IInstructionManager"/> 一并清理。</para>
    /// </summary>
    public InstructionFingerprintSnapshot GetFingerprints(SessionData session) =>
        new();
}
