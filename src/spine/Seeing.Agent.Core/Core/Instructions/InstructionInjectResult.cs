namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令注入结果：是否实际注入及原因说明。
/// <para>不含路径列表：路径属于源读取时的事实，已由源自带的元数据（消息
/// <c>instructionPaths</c>）承载，不在本类重复产出（避免「同一事实两条产出路径」）。</para>
/// </summary>
public sealed class InstructionInjectResult
{
    /// <summary>本次是否执行了注入</summary>
    public bool Injected { get; init; }

    /// <summary>注入或未注入的原因说明</summary>
    public string Reason { get; init; } = string.Empty;
}
