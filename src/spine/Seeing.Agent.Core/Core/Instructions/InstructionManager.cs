using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Context;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令管理器：<b>薄适配器</b>（迁移期保留；主链已由 <c>ExecutionRunner</c> 直接装配）。
/// <para>真相源唯一：指令内容、变更判定与注入位置全部由上下文管道负责；
/// 本类只做两件事——把调用方给出的执行根填进执行作用域，再把装配结果投影为
/// <see cref="InstructionInjectResult"/>。</para>
/// <para><b>生命周期纪律</b>：本类是单例，<see cref="IContextAssembler"/> 是 scoped，
/// 因此绝不能构造注入装配器（captive dependency）；每次调用创建短生命周期作用域，
/// 在其中填充作用域事实、解析装配器并完成一次装配。</para>
/// </summary>
public sealed class InstructionManager : IInstructionManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InstructionManager> _logger;

    /// <summary>
    /// 构造指令管理器。
    /// </summary>
    /// <param name="scopeFactory">作用域工厂（每次调用一个短作用域，避免捕获 scoped 装配器）。</param>
    /// <param name="logger">管理器日志。</param>
    public InstructionManager(
        IServiceScopeFactory scopeFactory,
        ILogger<InstructionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 触发一次 Persist 装配，并把结果投影为注入结果。
    /// <para><paramref name="cwd"/> / <paramref name="workspaceRoot"/> 先写入本次调用的执行作用域，
    /// 再由源读取，因此不会再出现「签名在、值被丢弃」。</para>
    /// </summary>
    public async Task<InstructionInjectResult> InjectIfNeededAsync(
        SessionData session,
        string cwd,
        string workspaceRoot,
        CancellationToken ct = default,
        string? insertBeforeMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var scope = _scopeFactory.CreateScope();

        var executionScope = scope.ServiceProvider.GetRequiredService<IContextExecutionScope>();
        executionScope.SessionId = session.Id;
        executionScope.Cwd = cwd;
        executionScope.WorkspaceRoot = workspaceRoot;
        // 迁移适配路径不组合技能模块上下文：快照保持 null，技能源按「不可用」处理
        // （保留上次已叙述内容，绝不把缺失误判成技能已删除）。

        var assembler = scope.ServiceProvider.GetRequiredService<IContextAssembler>();

        var result = await assembler.EnsureFreshAsync(
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
