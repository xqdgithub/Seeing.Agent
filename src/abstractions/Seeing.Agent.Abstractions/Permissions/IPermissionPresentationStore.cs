namespace Seeing.Agent.Abstractions.Permissions;

/// <summary>
/// 呈现端登记表：判定"是否存在可呈现指定会话的呈现端"的唯一入口。
/// </summary>
public interface IPermissionPresentationStore
{
    void Register(IPermissionPresenter presenter);

    void Unregister(IPermissionPresenter presenter);

    /// <summary>是否存在可呈现指定会话的呈现端。</summary>
    bool CanSurface(string sessionId);

    /// <summary>呈现端注销（身份移除）；用于在途收敛复核。注册不触发。</summary>
    event Action? PresenterUnregistered;

    /// <summary>呈现端集合或可呈现集合内容变化；用于 UI/诊断，不触发在途收敛。</summary>
    event Action? Changed;
}
