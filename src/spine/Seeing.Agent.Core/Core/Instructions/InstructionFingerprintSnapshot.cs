using System.Collections.ObjectModel;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 会话已注入指令文件的路径→内容指纹快照，用于检测指令变更。
/// </summary>
public sealed class InstructionFingerprintSnapshot
{
    /// <summary>
    /// 构造快照，文件字典按路径比较器归一化并设为只读。
    /// </summary>
    public InstructionFingerprintSnapshot(
        string? cwd = null,
        IReadOnlyDictionary<string, string>? files = null)
    {
        Cwd = cwd ?? string.Empty;
        Files = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                files ?? new Dictionary<string, string>(),
                InstructionFingerprintStore.PathComparer));
    }

    /// <summary>快照采集时的工作目录</summary>
    public string Cwd { get; }

    /// <summary>指令文件路径到内容指纹的映射</summary>
    public IReadOnlyDictionary<string, string> Files { get; }
}
