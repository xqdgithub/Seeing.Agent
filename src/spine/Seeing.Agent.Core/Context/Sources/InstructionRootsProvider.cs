using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Seeing.Agent.Core.Context.Sources;

/// <summary>
/// 指令扫描根：本轮执行 cwd + 工作区项目根。
/// <para>两者都是「执行作用域事实」：同一进程内不同会话 / 子会话的 cwd 不同，
/// 因此不得用进程当前目录代替（<see cref="InstructionDiscovery"/> 在 cwd 位于工作区外时
/// 只会返回用户级候选，项目指令会整块消失）。</para>
/// </summary>
/// <param name="Cwd">本轮执行工作目录（<c>ExecutionRunner</c> 提供的会话 cwd）。</param>
/// <param name="WorkspaceRoot">工作区项目根（<c>IWorkspaceProvider</c> 归属根）。</param>
public readonly record struct InstructionRoots(string Cwd, string WorkspaceRoot);

/// <summary>
/// 执行作用域端口：本轮指令源使用的扫描根。
/// <para>Task 12 起由 <c>ExecutionRunner</c> 在每轮执行作用域注册（scoped）；
/// 在此之前由 <see cref="InstructionManager"/> 用 <see cref="Push"/> 把调用方传入的 cwd 显式压入，
/// 保证「签名在、值被丢弃」的欺骗性 API 不再存在。</para>
/// </summary>
public interface IInstructionRootsProvider
{
    /// <summary>读取当前生效的扫描根（压入值优先，缺失时回退到执行世界 / 工作区提供者）。</summary>
    InstructionRoots GetRoots();

    /// <summary>
    /// 在当前异步流上覆盖扫描根（用于调用方持有会话 cwd 的路径）；释放返回值后恢复。
    /// <para>按异步流隔离：并发执行的不同会话互不影响。</para>
    /// </summary>
    IDisposable Push(InstructionRoots roots);
}

/// <summary>
/// 默认实现：显式压入优先；未压入时回退到 <see cref="IExecutionWorld.Cwd"/> +
/// <see cref="WorkspaceProviderExtensions.GetProjectRoot"/>，**不回退到进程当前目录**。
/// </summary>
public sealed class InstructionRootsProvider : IInstructionRootsProvider
{
    /// <summary>
    /// 压入帧（不可变链表 + 写时复制）：并发的不同会话各自压入互不影响，
    /// 子异步流也不会把父流的栈改坏。
    /// </summary>
    private static readonly AsyncLocal<RootsFrame?> s_frame = new();

    private readonly IWorkspaceProvider? _workspaceProvider;
    private readonly IExecutionWorld? _executionWorld;
    private readonly ILogger<InstructionRootsProvider> _logger;

    /// <summary>
    /// 创建根提供者。
    /// </summary>
    /// <param name="workspaceProvider">工作区提供者（可选：极简宿主可能未注册）。</param>
    /// <param name="executionWorld">执行世界（可选：提供本轮执行 cwd 的权威来源）。</param>
    /// <param name="logger">日志（缺失根时记录）。</param>
    public InstructionRootsProvider(
        IWorkspaceProvider? workspaceProvider = null,
        IExecutionWorld? executionWorld = null,
        ILogger<InstructionRootsProvider>? logger = null)
    {
        _workspaceProvider = workspaceProvider;
        _executionWorld = executionWorld;
        _logger = logger ?? NullLogger<InstructionRootsProvider>.Instance;
    }

    /// <inheritdoc />
    public InstructionRoots GetRoots()
    {
        var fallback = ResolveFallback();
        var frame = s_frame.Value;
        if (frame is null)
            return fallback;

        // 部分字段为空时按字段回退：调用方只知其一（如仅 cwd）也不会把另一个变成空串
        return new InstructionRoots(
            string.IsNullOrWhiteSpace(frame.Roots.Cwd) ? fallback.Cwd : frame.Roots.Cwd,
            string.IsNullOrWhiteSpace(frame.Roots.WorkspaceRoot) ? fallback.WorkspaceRoot : frame.Roots.WorkspaceRoot);
    }

    /// <inheritdoc />
    public IDisposable Push(InstructionRoots roots)
    {
        var parent = s_frame.Value;
        var current = new RootsFrame(roots, parent);
        s_frame.Value = current;
        return new PopScope(current, parent);
    }

    private InstructionRoots ResolveFallback()
    {
        var workspaceRoot = _workspaceProvider is null
            ? string.Empty
            : _workspaceProvider.GetProjectRoot();
        var cwd = _executionWorld?.Cwd ?? string.Empty;

        if (string.IsNullOrWhiteSpace(cwd) && string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug("指令扫描根缺失：既无压入值，也无执行世界 / 工作区提供者");
        }

        return new InstructionRoots(cwd, workspaceRoot);
    }

    /// <summary>压入帧。</summary>
    private sealed record RootsFrame(InstructionRoots Roots, RootsFrame? Parent);

    /// <summary>
    /// 出栈作用域：只有「当前帧仍是本次压入的帧」时才恢复父帧，
    /// 避免把子流后压入的值误清掉（幂等 Dispose）。
    /// </summary>
    private sealed class PopScope(RootsFrame pushed, RootsFrame? parent) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (ReferenceEquals(s_frame.Value, pushed))
                s_frame.Value = parent;
        }
    }
}
