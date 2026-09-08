namespace Seeing.Session.Hooks;

/// <summary>
/// Hook 管理器接口 - 用于 Session 生命周期钩子触发
/// </summary>
public interface IHookManager
{
    /// <summary>
    /// 触发非阻塞 Hook（fire-and-forget）
    /// </summary>
    /// <param name="hookPoint">Hook 点名称</param>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="input">输入数据（可选）</param>
    /// <param name="result">结果数据（可选）</param>
    void TriggerFireAndForget(
        string hookPoint,
        string sessionId,
        IReadOnlyDictionary<string, object?>? input = null,
        IReadOnlyDictionary<string, object?>? result = null);
}