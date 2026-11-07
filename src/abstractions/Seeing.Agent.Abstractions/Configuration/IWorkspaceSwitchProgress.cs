namespace Seeing.Agent.Configuration;

/// <summary>
/// 工作区切换的进度播报口。
/// <para>
/// 为什么放在契约层：切换本身在 <c>IWorkspaceSwitcher</c>（Hosting），
/// 而进度要显示在 UI（WebUI 的全局覆盖层）上。二者不能互相引用，
/// 于是把「报到哪一步了」抽成这个窄接口：开关由宿主提供，切换器只负责上报。
/// </para>
/// <para>无界面宿主（Headless / Gateway / CLI）不注册实现即可，切换器按可选依赖处理。</para>
/// </summary>
public interface IWorkspaceSwitchProgress
{
    /// <summary>开始一次切换（显示进度层）。</summary>
    /// <param name="message">初始说明文字。</param>
    void Begin(string message);

    /// <summary>更新当前阶段的说明文字。</summary>
    /// <param name="message">阶段说明。</param>
    void Report(string message);

    /// <summary>结束切换（隐藏进度层）。可重复调用。</summary>
    void End();
}
