namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令注入结果：是否实际注入及原因说明。
/// </summary>
public sealed class InstructionInjectResult
{
    /// <summary>本次是否执行了注入</summary>
    public bool Injected { get; init; }

    /// <summary>注入或未注入的原因说明</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>本次注入的指令文件路径列表</summary>
    public IReadOnlyList<string> InjectedPaths { get; init; } = Array.Empty<string>();
}
