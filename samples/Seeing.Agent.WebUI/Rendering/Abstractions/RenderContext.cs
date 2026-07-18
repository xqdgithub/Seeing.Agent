using Microsoft.AspNetCore.Components;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Rendering.Abstractions;

/// <summary>
/// 渲染上下文 - 包含渲染所需的所有信息
/// </summary>
/// <remarks>
/// <para>
/// 此类提供了渲染过程中需要的所有上下文信息，包括消息标识、流式状态、
/// 渲染选项、缓存服务和事件回调等。
/// </para>
/// <para>
/// ⚠️ <strong>线程安全说明：</strong>
/// 此类不是线程安全的。在 Blazor 组件渲染过程中，应该只在单个同步上下文中使用。
/// </para>
/// </remarks>
public class RenderContext
{
    /// <summary>
    /// 会话 ID（用于缓存隔离，防止跨会话缓存污染）
    /// </summary>
    /// <remarks>
    /// 必须在创建上下文时提供，用于构建唯一的缓存键。
    /// 通常使用 SessionState.SessionId 或类似的唯一标识。
    /// </remarks>
    public required string SessionId { get; init; }

    /// <summary>
    /// 当前消息 ID
    /// </summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// 当前 Loop ID（如果有）
    /// </summary>
    public string? LoopId { get; init; }

    /// <summary>
    /// 是否流式渲染
    /// </summary>
    public bool IsStreaming { get; init; }

    /// <summary>
    /// 渲染选项
    /// </summary>
    public required RenderOptions Options { get; init; }

    /// <summary>
    /// 服务提供者（用于依赖注入）
    /// </summary>
    public IServiceProvider? ServiceProvider { get; init; }

    /// <summary>
    /// 缓存服务
    /// </summary>
    public IRenderCache? Cache { get; init; }

    /// <summary>
    /// 工具点击回调
    /// </summary>
    /// <remarks>
    /// EventCallback 是 struct，使用 default 表示未设置。
    /// 在使用前检查 HasDelegate 属性。
    /// </remarks>
    public EventCallback<ToolCallViewModel> OnToolClick { get; init; }

    /// <summary>
    /// 打开 Task 子会话回调（taskId ≡ Child Session Id）
    /// </summary>
    public EventCallback<string> OnOpenTaskSession { get; init; }

    /// <summary>
    /// 生成结构化缓存键
    /// </summary>
    /// <param name="block">内容块</param>
    /// <param name="suffix">可选的后缀（用于同一块的多个缓存项）</param>
    /// <returns>格式化的缓存键，格式为 "SessionId:MessageId:BlockType:BlockId[:Suffix]"</returns>
    /// <remarks>
    /// <para>
    /// 缓存键格式说明：
    /// <list type="bullet">
    ///   <item><description>SessionId: 会话标识，确保不同会话的缓存隔离</description></item>
    ///   <item><description>MessageId: 消息标识，确保同一会话内消息隔离</description></item>
    ///   <item><description>BlockType: 内容块类型，快速区分不同类型</description></item>
    ///   <item><description>BlockId: 内容块唯一标识，使用确定性 ID</description></item>
    ///   <item><description>Suffix: 可选后缀，用于同一块的多个缓存项</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public string GetCacheKey(ContentBlock block, string? suffix = null)
    {
        var key = $"{SessionId}:{MessageId}:{block.Type}:{block.Id}";
        return suffix != null ? $"{key}:{suffix}" : key;
    }

    /// <summary>
    /// 生成 Markdown 渲染缓存键
    /// </summary>
    /// <param name="block">内容块</param>
    /// <returns>缓存键</returns>
    public string GetMarkdownCacheKey(ContentBlock block)
    {
        return GetCacheKey(block, "md");
    }

    /// <summary>
    /// 创建子上下文（用于嵌套渲染，如子代理内容）
    /// </summary>
    /// <param name="messageId">新消息 ID</param>
    /// <returns>新的渲染上下文</returns>
    public RenderContext CreateChild(string messageId)
    {
        return new RenderContext
        {
            SessionId = SessionId,
            MessageId = messageId,
            LoopId = LoopId,
            IsStreaming = IsStreaming,
            Options = Options,
            ServiceProvider = ServiceProvider,
            Cache = Cache,
            OnToolClick = OnToolClick,
            OnOpenTaskSession = OnOpenTaskSession
        };
    }

    /// <summary>
    /// 从消息视图模型创建渲染上下文
    /// </summary>
    /// <param name="message">消息视图模型</param>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="options">渲染选项（可选）</param>
    /// <returns>渲染上下文</returns>
    public static RenderContext FromMessage(
        MessageViewModel message,
        string sessionId,
        RenderOptions? options = null)
    {
        return new RenderContext
        {
            SessionId = sessionId,
            MessageId = message.Id,
            LoopId = message.LoopId,
            IsStreaming = !message.IsComplete,
            Options = options ?? new RenderOptions()
        };
    }
}

/// <summary>
/// 渲染选项
/// </summary>
public class RenderOptions
{
    /// <summary>
    /// 是否显示推理过程
    /// </summary>
    public bool ShowReasoning { get; set; } = true;

    /// <summary>
    /// 是否默认展开推理
    /// </summary>
    public bool ExpandReasoningByDefault { get; set; } = false;

    /// <summary>
    /// 是否显示工具调用
    /// </summary>
    public bool ShowToolCalls { get; set; } = true;

    /// <summary>
    /// 是否显示附件
    /// </summary>
    public bool ShowAttachments { get; set; } = true;

    /// <summary>
    /// 是否显示时间戳
    /// </summary>
    public bool ShowTimestamp { get; set; } = true;

    /// <summary>
    /// Markdown 渲染选项
    /// </summary>
    public MarkdownRenderOptions Markdown { get; set; } = new();

    /// <summary>
    /// 工具调用显示选项
    /// </summary>
    public ToolCallRenderOptions ToolCalls { get; set; } = new();

    /// <summary>
    /// 默认选项
    /// </summary>
    public static RenderOptions Default => new();

    /// <summary>
    /// 紧凑模式（隐藏推理、工具详情）
    /// </summary>
    public static RenderOptions Compact => new()
    {
        ShowReasoning = false,
        ExpandReasoningByDefault = false,
        ToolCalls = new ToolCallRenderOptions { ShowDetails = false }
    };
}

/// <summary>
/// Markdown 渲染选项
/// </summary>
public class MarkdownRenderOptions
{
    /// <summary>
    /// 是否启用语法高亮
    /// </summary>
    public bool EnableSyntaxHighlighting { get; set; } = true;

    /// <summary>
    /// 是否启用数学公式
    /// </summary>
    public bool EnableMath { get; set; } = false;

    /// <summary>
    /// 是否启用任务列表
    /// </summary>
    public bool EnableTaskLists { get; set; } = true;

    /// <summary>
    /// 是否启用自动链接
    /// </summary>
    public bool EnableAutoLinks { get; set; } = true;

    /// <summary>
    /// 代码块最大高度
    /// </summary>
    public string CodeBlockMaxHeight { get; set; } = "400px";
}

/// <summary>
/// 工具调用渲染选项
/// </summary>
public class ToolCallRenderOptions
{
    /// <summary>
    /// 是否显示详情
    /// </summary>
    public bool ShowDetails { get; set; } = true;

    /// <summary>
    /// 是否默认展开
    /// </summary>
    public bool ExpandByDefault { get; set; } = false;

    /// <summary>
    /// 结果最大长度
    /// </summary>
    public int MaxResultLength { get; set; } = 500;

    /// <summary>
    /// 参数最大长度
    /// </summary>
    public int MaxParameterLength { get; set; } = 200;
}
