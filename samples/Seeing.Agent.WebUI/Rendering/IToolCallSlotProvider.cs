namespace Seeing.Agent.WebUI.Rendering;

/// <summary>
/// 工具调用内联槽位扩展点：在工具调用块之后按需追加渲染的组件。
/// </summary>
/// <remarks>
/// <para>
/// 管线不感知具体槽位语义（如权限审批、交互提问）；各能力通过实现此接口注册自己的槽位组件。
/// </para>
/// <para>
/// ⚠️ 实现必须无状态，且由 DI 以 Scoped 生命周期注册。
/// </para>
/// </remarks>
public interface IToolCallSlotProvider
{
    /// <summary>
    /// 渲染顺序（数值越小越先渲染）。
    /// </summary>
    int Order { get; }

    /// <summary>
    /// 获取工具调用后要渲染的槽位组件类型。
    /// </summary>
    /// <param name="callId">工具调用 ID</param>
    /// <returns>槽位组件类型；返回 null 表示该工具调用无需渲染槽位</returns>
    Type? GetSlotComponentType(string callId);
}
