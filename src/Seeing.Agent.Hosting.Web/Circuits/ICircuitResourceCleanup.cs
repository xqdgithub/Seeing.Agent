namespace Seeing.Agent.Hosting.Web.Circuits;

/// <summary>
/// Circuit 关闭时释放宿主侧资源（事件流订阅、Scoped consumer 等）。
/// 由 sample 将 SessionEventStreamRouter 等实现注册到 DI。
/// </summary>
public interface ICircuitResourceCleanup
{
    void DetachAllForCircuit(string circuitId);
}
