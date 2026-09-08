namespace Seeing.Agent.Hosting.Web.Circuits;

/// <summary>
/// 当前 circuit 的 Id 载体（Scoped）。SeeingCircuitHandler 在 OnCircuitOpenedAsync 写入，
/// 页面在自身 scope 内读取后传给事件流路由器，使 Singleton Router 能按 circuit 关联 consumer。
/// </summary>
public sealed class CircuitContext
{
    public string? Id { get; set; }
}
