namespace Seeing.Agent.Abstractions.Tools;

/// <summary>
/// 工具对当前 Agent Loop 的轮次指令。
/// </summary>
public enum ToolTurnDirective
{
    /// <summary>继续循环（默认）</summary>
    Continue = 0,

    /// <summary>结束当前 Loop（全部工具结束后不再进行下一轮 LLM 调用）</summary>
    EndTurn = 1
}

/// <summary>
/// 文件附件
/// </summary>
public class FileAttachment
{
    /// <summary>文件名</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>文件路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>MIME 类型</summary>
    public string MimeType { get; set; } = string.Empty;

    /// <summary>文件大小（字节）</summary>
    public long? Size { get; set; }
}

/// <summary>
/// 工具执行结果 - 统一的工具调用返回类型
/// <para>
/// 字段命名参考业界惯例：
/// - OpenAI/Anthropic: content
/// - LangChain: output, error
/// - Semantic Kernel: result
/// </para>
/// </summary>
public class ToolResult
{
    /// <summary>是否成功执行</summary>
    public bool Success { get; set; }

    /// <summary>
    /// 输出内容（成功时）
    /// <para>对应 OpenAI 的 content，LangChain 的 output</para>
    /// </summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>标题/工具调用名称（用于显示与追踪）</summary>
    public string? Title { get; set; }

    /// <summary>
    /// 错误信息（失败时）
    /// <para>对应 LangChain 的 error</para>
    /// </summary>
    public string? Error { get; set; }

    /// <summary>关联的工具调用 ID</summary>
    public string? ToolCallId { get; set; }

    /// <summary>元数据</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>文件附件</summary>
    [Obsolete("改用 Parts（ToolOutputPart）：框架会把 Parts 物化后交给模型，Attachments 无人消费。")]
    public List<FileAttachment>? Attachments { get; set; }

    /// <summary>
    /// 产出的内容块（文本 / 图片 / 文件）。
    /// <para>
    /// 框架在 <c>Session → Chat</c> 转换处把 <see cref="ToolOutputPart.LocalPath"/> 物化为可发送载荷，
    /// 会话只持久化引用（不存 base64）。出站形状（内嵌 / 拆消息）由各 LLM client 决定。
    /// </para>
    /// </summary>
    public IReadOnlyList<ToolOutputPart>? Parts { get; set; }

    /// <summary>
    /// 工具声明"本执行内已加载"的工具 id。
    /// <para>
    /// 仅当工具声明了 <see cref="ToolCapabilityKeys.ExposureLoad"/> 能力时才会被执行器采纳；
    /// 执行器据此放宽本次执行的可调用集（不改变 <c>tools[]</c>）。
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? LoadedToolIds { get; set; }

    /// <summary>
    /// 本调用转交的后台活动 id（如 <c>bash background=true</c>）。
    /// <para>
    /// 仅当工具把执行转交后台活动时填充；调用本身仍按原语义返回终态（不新增状态）。
    /// 这是强类型投影，禁止改用 <see cref="Metadata"/> 字符串约定传递。
    /// </para>
    /// </summary>
    public string? BackgroundActivityId { get; set; }

    /// <summary>
    /// 调用是否「转成了后台」（等待者被放行、活动继续跑）。
    /// <para>
    /// 前台正常完成与转后台在 <see cref="BackgroundActivityOrigin.ToolForeground"/> 下无法区分：
    /// 两者都是工具已结束、活动已有 id。此标记为 UI 提供「输出应取工具结果」还是
    /// 「取活动实时尾部」的唯一判据，禁止靠活动状态 + 工具状态猜测。
    /// </para>
    /// </summary>
    public bool BackgroundDetached { get; init; }

    /// <summary>执行耗时</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>本轮轮次指令：EndTurn 时 AgentExecutor 在全部工具结束后终止 Loop（默认 Continue）</summary>
    public ToolTurnDirective TurnDirective { get; set; } = ToolTurnDirective.Continue;

    /// <summary>轮次指令原因（用于诊断与 LoopComplete.Reason）</summary>
    public string? TurnDirectiveReason { get; set; }

    /// <summary>
    /// 创建成功结果
    /// </summary>
    public static ToolResult Succeeded(string output)
    {
        return new ToolResult
        {
            Success = true,
            Output = output
        };
    }

    /// <summary>
    /// 创建失败结果
    /// </summary>
    public static ToolResult Failed(string error)
    {
        return new ToolResult
        {
            Success = false,
            Error = error
        };
    }
}
