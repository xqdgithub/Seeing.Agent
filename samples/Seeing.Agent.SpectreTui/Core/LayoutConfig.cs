namespace Seeing.Agent.SpectreTui.Core;

/// <summary>
/// TUI 布局配置常量
/// </summary>
public static class LayoutConfig
{
    /// <summary>
    /// 消息区域比例（相对于总高度）
    /// </summary>
    public const int MessageAreaRatio = 4;

    /// <summary>
    /// 输入区域固定高度（行数）
    /// </summary>
    public const int InputAreaSize = 3;

    /// <summary>
    /// 状态栏固定高度（行数）
    /// </summary>
    public const int StatusBarSize = 1;

    /// <summary>
    /// 顶部标题栏高度
    /// </summary>
    public const int HeaderSize = 1;

    /// <summary>
    /// 消息历史最大条数
    /// </summary>
    public const int MaxMessageHistory = 100;

    /// <summary>
    /// 流式输出刷新间隔（毫秒）
    /// </summary>
    public const int StreamRefreshIntervalMs = 100;

    /// <summary>
    /// 批量事件处理阈值
    /// </summary>
    public const int EventBatchThreshold = 10;

    /// <summary>
    /// 通道缓冲区大小
    /// </summary>
    public const int ChannelBufferSize = 1000;

    /// <summary>
    /// 终端最小宽度
    /// </summary>
    public const int MinTerminalWidth = 80;

    /// <summary>
    /// 终端最小高度
    /// </summary>
    public const int MinTerminalHeight = 24;
}

/// <summary>
/// 颜色方案配置
/// </summary>
public static class ColorScheme
{
    // 主色调
    public const string PrimaryColor = "blue";
    public const string SecondaryColor = "cyan";
    public const string SuccessColor = "green";
    public const string WarningColor = "yellow";
    public const string ErrorColor = "red";
    public const string InfoColor = "grey";

    // 消息角色颜色
    public const string UserMessageColor = "green";
    public const string AssistantMessageColor = "blue";
    public const string SystemMessageColor = "yellow";
    public const string ToolMessageColor = "purple";

    // 工具状态颜色
    public const string ToolPendingColor = "yellow";
    public const string ToolRunningColor = "blue";
    public const string ToolSuccessColor = "green";
    public const string ToolFailedColor = "red";
    public const string ToolRejectedColor = "grey";

    // UI 元素颜色
    public const string BorderColor = "grey";
    public const string HeaderColor = "white on blue";
    public const string InputPromptColor = "green";
    public const string ReasoningColor = "grey italic";
}

/// <summary>
/// Spinner 类型选择
/// </summary>
public static class SpinnerConfig
{
    /// <summary>
    /// 默认 Spinner 类型
    /// </summary>
    public static Spectre.Console.Spinner DefaultSpinner => Spectre.Console.Spinner.Known.Dots;

    /// <summary>
    /// 处理中 Spinner
    /// </summary>
    public static Spectre.Console.Spinner ProcessingSpinner => Spectre.Console.Spinner.Known.Star;

    /// <summary>
    /// 等待中 Spinner
    /// </summary>
    public static Spectre.Console.Spinner WaitingSpinner => Spectre.Console.Spinner.Known.Clock;
}

/// <summary>
/// 布局区域名称
/// </summary>
public static class LayoutRegions
{
    public const string Root = "Root";
    public const string Header = "Header";
    public const string Body = "Body";
    public const string Messages = "Messages";
    public const string Input = "Input";
    public const string Footer = "Footer";
}
