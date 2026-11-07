namespace Seeing.Agent.Configuration;

/// <summary>
/// 最近工作区历史。读-改-写跨进程加锁，<c>recent</c> 按路径合并。
/// </summary>
public interface IWorkspaceHistoryStore
{
    /// <summary>当前快照。文件损坏或不存在时返回空快照，不抛异常。</summary>
    WorkspaceHistorySnapshot Read();

    /// <summary>
    /// 记录一次打开。<paramref name="asDefault"/> 为真时同时更新 <c>lastInteractive</c>。
    /// 写入失败只记日志，不抛出。
    /// </summary>
    void RecordOpened(string path, bool asDefault);

    /// <summary>
    /// 从最近列表移除一个目录；若它正是 <c>lastInteractive</c>，一并清空。
    /// 只影响历史，不触碰磁盘。写入失败只记日志，不抛出。
    /// </summary>
    void Remove(string path);
}
