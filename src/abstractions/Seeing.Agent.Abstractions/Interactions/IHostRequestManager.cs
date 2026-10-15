namespace Seeing.Agent.Abstractions.Interactions;

/// <summary>宿主请求在途管理器契约（登记/等待/幂等完成/收敛）。</summary>
public interface IHostRequestManager : IPendingRequestManager<HostRequest, HostResponse>
{
}
