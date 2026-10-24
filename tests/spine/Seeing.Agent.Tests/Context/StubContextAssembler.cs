using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Core.Context;
using Seeing.Session.Core;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 测试用装配器存根：默认不读源、不叙述、返回空基线。
/// <para>用于只关心「是否被调用 / 以什么参数被调用」的执行链测试；需要真实装配语义时由测试自行注册。</para>
/// </summary>
internal class StubContextAssembler : IContextAssembler
{
    /// <summary>空装配结果（无基线、无增量、无问题）。</summary>
    public static ContextAssemblyResult EmptyResult { get; } =
        new(string.Empty, null, null, [], [], []);

    /// <inheritdoc />
    public virtual Task<ContextAssemblyResult> EnsureFreshAsync(
        SessionData session,
        ContextAssemblyMode mode,
        string? insertBeforeMessageId,
        IReadOnlySet<ContextSourceKey>? skip,
        CancellationToken ct = default) => Task.FromResult(EmptyResult);

    /// <inheritdoc />
    public virtual string BuildSystemBaseline(SessionData session) => string.Empty;
}

/// <summary>一次装配调用的参数快照。</summary>
internal sealed record AssemblyCall(
    ContextAssemblyMode Mode,
    string? InsertBeforeMessageId,
    IReadOnlySet<ContextSourceKey>? Skip);

/// <summary>
/// 记录调用参数的装配器假实现：可观察执行作用域里的钉住快照与 cwd，
/// 并允许测试注入「装配时对会话做什么」（如插入增量消息）。
/// </summary>
internal sealed class RecordingContextAssembler : IContextAssembler
{
    /// <summary>由 DI 工厂在创建时注入的执行级作用域（与装配器同 scope）。</summary>
    public IContextExecutionScope? Scope { get; set; }

    /// <summary>全部装配调用的参数（按调用顺序）。</summary>
    public List<AssemblyCall> Calls { get; } = [];

    /// <summary><see cref="BuildSystemBaseline"/> 与只读装配返回值使用的基线文本。</summary>
    public string Baseline { get; set; } = string.Empty;

    /// <summary>装配时作用域里的钉住快照（用于断言「装配前作用域已被填充」）。</summary>
    public SkillCatalogSnapshot? ObservedSnapshot { get; private set; }

    /// <summary>装配时作用域里的 cwd。</summary>
    public string? ObservedCwd { get; private set; }

    /// <summary>装配时作用域里的工作区根。</summary>
    public string? ObservedWorkspaceRoot { get; private set; }

    /// <summary>装配时的副作用（测试注入）。</summary>
    public Action<SessionData, ContextAssemblyMode>? OnAssemble { get; set; }

    /// <inheritdoc />
    public Task<ContextAssemblyResult> EnsureFreshAsync(
        SessionData session,
        ContextAssemblyMode mode,
        string? insertBeforeMessageId,
        IReadOnlySet<ContextSourceKey>? skip,
        CancellationToken ct = default)
    {
        Calls.Add(new AssemblyCall(mode, insertBeforeMessageId, skip));
        ObservedSnapshot = Scope?.SkillSnapshot;
        ObservedCwd = Scope?.Cwd;
        ObservedWorkspaceRoot = Scope?.WorkspaceRoot;
        OnAssemble?.Invoke(session, mode);
        return Task.FromResult(new ContextAssemblyResult(Baseline, null, null, [], [], []));
    }

    /// <inheritdoc />
    public string BuildSystemBaseline(SessionData session) => Baseline;
}

/// <summary>
/// 测试用执行级上下文管道的注册助手：与 <c>AddContextPipeline</c> 保持同一生命周期形状
/// （执行作用域实例 + scoped 装配器），使 <c>ExecutionRunner</c> 的 scoped 解析路径与生产一致。
/// </summary>
internal static class ContextPipelineTestServices
{
    /// <summary>
    /// 注册 scoped <see cref="ContextExecutionScope"/>、<see cref="IContextExecutionScope"/> 与装配器。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="assembler">可选装配器实例（缺省为 <see cref="StubContextAssembler"/>）。</param>
    public static IServiceCollection AddStubContextPipeline(
        this IServiceCollection services,
        IContextAssembler? assembler = null)
    {
        services.AddScoped<ContextExecutionScope>();
        services.AddScoped<IContextExecutionScope>(sp => sp.GetRequiredService<ContextExecutionScope>());
        services.AddScoped<IContextAssembler>(sp =>
        {
            if (assembler is RecordingContextAssembler recording)
            {
                // 注入同一 scope 的作用域实例：测试据此观察「装配前作用域已被填充」
                recording.Scope = sp.GetRequiredService<IContextExecutionScope>();
                return recording;
            }

            return assembler ?? new StubContextAssembler();
        });
        return services;
    }
}
