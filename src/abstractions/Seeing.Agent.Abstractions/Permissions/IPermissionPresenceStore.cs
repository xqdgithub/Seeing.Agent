namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>会话可交互计数。Attach 来源受限：
/// WebUI=**活动会话的页面级 UI**；Gateway=**外部客户端**会话订阅。
/// 不得以 TaskCardAggregator/子会话/编排器内部订阅/TaskTool 后台订阅记账。</summary>
public interface IPermissionPresenceStore
{
    void Attach(string sessionId);

    void Detach(string sessionId);

    bool CanPresent(string sessionId);

    void ClearAll();
}
