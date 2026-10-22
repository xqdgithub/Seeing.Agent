namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具发现态写入端口：把执行中途检索到的工具登记为"待下一轮生效"。
/// <para>
/// 只有写入，没有读取与裁剪：读取由结算视图提供，裁剪由执行入口在轮首完成。
/// 实现负责按会话实例写入会话级暴露状态，不触发落盘（由执行链统一保存）。
/// </para>
/// </summary>
public interface IToolDiscoveryStore
{
    /// <summary>
    /// 登记待生效发现。返回是否全部写入成功；会话不存在时返回 false（不得静默成功）。
    /// </summary>
    bool Add(string sessionId, IEnumerable<string> toolIds);
}
