namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具暴露策略的**配置**模式（四级配置解析后的结果，允许 <see cref="Auto"/> 这种"待判定"值）。
/// 与运行期结果态 <see cref="ToolExposureLayout"/> 区分：后者不含 Auto。
/// </summary>
public enum ToolExposurePolicyMode
{
    /// <summary>全部内联。</summary>
    All,

    /// <summary>按阈值与会话粘滞自动在全部内联与分层之间选择。</summary>
    Auto,

    /// <summary>恒定分层。</summary>
    Tiered
}
