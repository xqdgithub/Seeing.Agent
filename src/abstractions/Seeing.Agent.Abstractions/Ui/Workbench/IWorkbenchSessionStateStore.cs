namespace Seeing.Agent.Abstractions.Ui.Workbench;

/// <summary>
/// 会话工作台快照存储（UI 推送，服务端只读）。
/// <para>
/// <b>为什么服务端不自己算</b>：工作台是 UI 侧概念，开合/激活/内部查看内容只有浏览器知道。
/// 服务端需要知道的只有一个稳定事实 —— 「用户现在打开着哪些工作台、在看什么」，
/// 供 agent 列目录与上下文源消费。因此本端口是<b>单向</b>的：UI 写，服务端读。
/// </para>
/// <para>写入方须推送<b>已确认的稳定快照</b>（UI 侧已去抖），不得每帧写。</para>
/// </summary>
public interface IWorkbenchSessionStateStore
{
    /// <summary>整批替换某会话的快照（幂等；空列表等价于「全部关闭」）。</summary>
    void Update(string sessionId, IReadOnlyList<WorkbenchSessionSnapshot> snapshots);

    /// <summary>清空某会话的快照（会话删除 / 页面卸载）。</summary>
    void Clear(string sessionId);

    /// <summary>读取某会话的快照；未知会话返回空列表（不是 null）。</summary>
    IReadOnlyList<WorkbenchSessionSnapshot> GetSnapshot(string sessionId);

    /// <summary>快照变化通知（参数为会话 id）。</summary>
    event Action<string>? Changed;
}
