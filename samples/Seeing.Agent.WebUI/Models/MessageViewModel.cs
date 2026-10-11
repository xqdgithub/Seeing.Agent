namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// 消息视图模型，用于 UI 显示
/// </summary>
public class MessageViewModel
{
    /// <summary>
    /// 消息唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 消息所属会话 ID（消息当前所在会话；Fork/分支复制时改写为新会话 Id）
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Agent Loop ID（一次完整对话循环的唯一标识）
    /// <para>
    /// 用于关联一次 Agent 交互中产生的所有消息，便于前端按对话单元渲染。
    /// </para>
    /// </summary>
    public string? LoopId { get; set; }

    /// <summary>
    /// 步骤索引（同一 LoopId 内的顺序，step=0, 1, 2...）
    /// <para>
    /// 一次 Agent Loop 可能包含多轮 LLM 调用（工具调用前后），
    /// Step 用于区分不同轮次的思考、工具调用和回复内容。
    /// </para>
    /// </summary>
    public int Step { get; set; }

    /// <summary>
    /// 消息角色 (user/assistant/system/tool)
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// 消息内容
    /// </summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// 推理过程（如果支持）
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// 推理/思考过程是否已完成
    /// <para>
    /// 在流式输出中，推理阶段可能先于整体消息完成。
    /// 当推理内容停止更新时（LLM 进入工具调用或文本生成阶段），应标记为 true。
    /// </para>
    /// </summary>
    public bool IsReasoningComplete { get; set; }

    /// <summary>
    /// 消息时间戳
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 工具调用列表
    /// </summary>
    public List<ToolCallViewModel> ToolCalls { get; set; } = new();

    /// <summary>
    /// 多模态内容段列表（图片、文件等附件）
    /// </summary>
    public List<ContentPartViewModel> Parts { get; set; } = new();

    /// <summary>
    /// 消息是否已完成
    /// </summary>
    public bool IsComplete { get; set; }

    /// <summary>
    /// 是否是压缩摘要消息
    /// </summary>
    public bool IsCompactionSummary { get; set; }

    /// <summary>
    /// 是否已被压缩替代（历史保留可查看，但不传递给 LLM，UI 折叠展示）
    /// </summary>
    public bool IsCompacted { get; set; }

    /// <summary>
    /// 是否为系统提醒（Job / Task 回调等注入的 user 消息）
    /// </summary>
    public bool IsSystemReminder { get; set; }

    public string? ReminderSource { get; set; }
    public string? ReminderKind { get; set; }
    public string ReminderTaskBody { get; set; } = "";
    public string ReminderRaw { get; set; } = "";

    /// <summary>
    /// 是否为项目指令注入（AGENTS.md 渐进加载）
    /// </summary>
    public bool IsProjectInstructions { get; set; }

    public string? InstructionReason { get; set; }
    public string? InstructionCwd { get; set; }
    public IReadOnlyList<string> InstructionPaths { get; set; } = Array.Empty<string>();
    public string InstructionRaw { get; set; } = "";

    /// <summary>
    /// 是否包含附件
    /// </summary>
    public bool HasAttachments => Parts != null && Parts.Count > 0;

    /// <summary>
    /// 是否包含图片
    /// </summary>
    public bool HasImages => Parts?.Any(p => p.IsImage) ?? false;

    /// <summary>
    /// 是否包含文件（非图片）
    /// </summary>
    public bool HasFiles => Parts?.Any(p => p.IsFile) ?? false;

    // ---- 入站来源（inbox.* 元数据投影；仅入站消息非空） ----

    /// <summary>入站发送方类型：human | session | system（null = 非入站消息）。</summary>
    public string? InboxSenderKind { get; set; }

    /// <summary>入站发送方标识（来源会话 Id / 子系统标签）。</summary>
    public string? InboxSenderId { get; set; }

    /// <summary>入站发送方显示名。</summary>
    public string? InboxSenderName { get; set; }

    /// <summary>入站来源标签（task/cron/…）。</summary>
    public string? InboxSource { get; set; }

    /// <summary>入站种类（completed/failed/…）。</summary>
    public string? InboxKind { get; set; }

    /// <summary>入站呈现身份：user | system | assistant | none。</summary>
    public string? InboxPresentation { get; set; }

    /// <summary>是否为待消费（排队中）的入站 user 消息。</summary>
    public bool IsPendingInbox { get; set; }

    /// <summary>是否为非人类来源的入站消息（用于来源徽章与视觉区分）。</summary>
    public bool IsExternalInbox =>
        !string.IsNullOrEmpty(InboxSenderKind)
        && !string.Equals(InboxSenderKind, "human", StringComparison.OrdinalIgnoreCase);

    /// <summary>来源徽章文本（非人类来源）；否则 null。</summary>
    public string? InboxBadgeText => InboxSenderKind?.ToLowerInvariant() switch
    {
        "session" => "子会话",
        "system" => "系统",
        _ => null
    };

    /// <summary>来源徽章悬停提示（含 inbox.* 元数据）。</summary>
    public string InboxTooltip
    {
        get
        {
            var lines = new List<string> { $"来源: {InboxSenderKind ?? "-"}" };
            if (!string.IsNullOrEmpty(InboxSenderName))
                lines.Add($"名称: {InboxSenderName}");
            if (!string.IsNullOrEmpty(InboxSenderId))
                lines.Add($"inbox.sender_id: {InboxSenderId}");
            if (!string.IsNullOrEmpty(InboxPresentation))
                lines.Add($"inbox.presentation: {InboxPresentation}");
            if (!string.IsNullOrEmpty(InboxSource))
                lines.Add($"inbox.source: {InboxSource}");
            if (!string.IsNullOrEmpty(InboxKind))
                lines.Add($"inbox.kind: {InboxKind}");
            return string.Join("\n", lines);
        }
    }
}