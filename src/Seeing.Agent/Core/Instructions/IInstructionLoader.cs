namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令加载器接口
/// </summary>
public interface IInstructionLoader
{
    /// <summary>
    /// 加载 AGENTS.md 文件
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>指令文件，若不存在则返回 null</returns>
    Task<InstructionFile?> LoadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜索目录中的 AGENTS.md 文件
    /// 搜索顺序：当前目录 -> 父目录 -> 用户主目录/.agents/
    /// </summary>
    /// <param name="baseDirectory">起始目录，默认为当前工作目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>找到的所有指令文件</returns>
    Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string? baseDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 合并多个指令文件内容
    /// </summary>
    /// <param name="files">指令文件集合</param>
    /// <returns>合并后的内容</returns>
    string Merge(IEnumerable<InstructionFile> files);
}