namespace Seeing.Agent.Abstractions.Ui;

/// <summary>
/// 模块 UI 贡献契约。仅 Web Host Shape 消费；Headless / Gateway 不加载。
/// </summary>
public interface IUiContribution
{
    /// <summary>贡献方模块 id。</summary>
    string ModuleId { get; }

    /// <summary>
    /// 返回本模块的 UI 贡献列表。
    /// 元素类型为 <see cref="NavContribution"/>、<see cref="SettingsCardContribution"/>、
    /// <see cref="SlotContribution"/> 或 <see cref="MessageRendererContribution"/>。
    /// </summary>
    IReadOnlyList<object> Contribute();
}
