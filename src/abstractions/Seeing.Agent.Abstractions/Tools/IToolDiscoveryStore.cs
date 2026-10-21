namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具发现记录存取端口：按会话记录已发现（声明过）的工具 ID（纯数据存取，无业务规则）。
/// </summary>
public interface IToolDiscoveryStore
{
    /// <summary>获取指定会话已发现的工具 ID 集合。</summary>
    IReadOnlySet<string> Get(string sessionId);

    /// <summary>为指定会话追加已发现的工具 ID。</summary>
    void Add(string sessionId, IEnumerable<string> toolIds);

    /// <summary>以当前可用工具集裁剪指定会话的发现记录（移除不再可用的 ID）。</summary>
    void Prune(string sessionId, IReadOnlySet<string> availableToolIds);
}
