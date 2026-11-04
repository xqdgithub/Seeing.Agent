namespace Seeing.Agent.WebUI.Models.Messaging;

/// <summary>
/// 消息内容块类型
/// </summary>
public enum ContentBlockType
{
    /// <summary>
    /// 思考/推理过程
    /// </summary>
    Reasoning,

    /// <summary>
    /// 文本内容
    /// </summary>
    Text,

    /// <summary>
    /// 工具调用
    /// </summary>
    ToolCall,

    /// <summary>
    /// 附件
    /// </summary>
    Attachment,

    /// <summary>
    /// 图片
    /// </summary>
    Image,

    /// <summary>
    /// 错误信息
    /// </summary>
    Error,

    /// <summary>
    /// 子代理
    /// </summary>
    SubAgent,

    /// <summary>
    /// 权限请求
    /// </summary>
    Permission,

    /// <summary>
    /// 分隔线（Step 之间）
    /// </summary>
    Divider
}

/// <summary>
/// 消息内容块 - 表示消息中的一个内容单元
/// </summary>
public class ContentBlock
{
    /// <summary>
    /// 块唯一标识（确定性生成）
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// 块类型
    /// </summary>
    public ContentBlockType Type { get; set; }

    /// <summary>
    /// 内容（文本/推理）
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// 工具调用数据（仅 ToolCall 类型）
    /// </summary>
    public ToolCallViewModel? ToolCall { get; set; }

    /// <summary>
    /// 附件数据（仅 Attachment/Image 类型）
    /// </summary>
    public ContentPartViewModel? Attachment { get; set; }

    /// <summary>
    /// 排序索引
    /// </summary>
    public int SortIndex { get; set; }

    /// <summary>
    /// 是否已完成（流式渲染时使用）
    /// </summary>
    public bool IsComplete { get; set; } = true;

    /// <summary>
    /// 是否正在流式输出
    /// </summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// 扩展数据（用于传递额外信息，如子代理名称、权限请求等）
    /// </summary>
    public Dictionary<string, object>? Extensions { get; set; }

    // ========== 确定性 ID 生成 ==========

    /// <summary>
    /// 生成确定性 ID
    /// </summary>
    public static string GenerateId(ContentBlockType type, int sortIndex, string? additionalKey = null)
    {
        return additionalKey != null
            ? $"{type.ToString().ToLowerInvariant()}-{sortIndex}-{SanitizeKey(additionalKey)}"
            : $"{type.ToString().ToLowerInvariant()}-{sortIndex}";
    }

    /// <summary>
    /// 清理 key 中的特殊字符
    /// </summary>
    private static string SanitizeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "unknown";
        // 只保留字母、数字、连字符和下划线，截取前 16 个字符
        var sanitized = new string(key.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return sanitized.Length > 16 ? sanitized[..16] : sanitized;
    }

    // ========== 工厂方法 ==========

    /// <summary>
    /// 创建思考内容块
    /// </summary>
    public static ContentBlock CreateReasoning(string? content, int sortIndex, bool isComplete = true)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Reasoning, sortIndex),
            Type = ContentBlockType.Reasoning,
            Content = content,
            SortIndex = sortIndex,
            IsComplete = isComplete,
            IsStreaming = !isComplete
        };
    }

    /// <summary>
    /// 创建文本内容块
    /// </summary>
    public static ContentBlock CreateText(string? content, int sortIndex, bool isComplete = true)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Text, sortIndex),
            Type = ContentBlockType.Text,
            Content = content,
            SortIndex = sortIndex,
            IsComplete = isComplete,
            IsStreaming = !isComplete
        };
    }

    /// <summary>
    /// 创建工具调用块
    /// </summary>
    public static ContentBlock CreateToolCall(ToolCallViewModel toolCall, int sortIndex)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.ToolCall, sortIndex, toolCall.Id),
            Type = ContentBlockType.ToolCall,
            ToolCall = toolCall,
            SortIndex = sortIndex,
            IsComplete = toolCall.Status != "running" && toolCall.Status != "pending"
        };
    }

    /// <summary>
    /// 创建附件块
    /// </summary>
    public static ContentBlock CreateAttachment(ContentPartViewModel attachment, int sortIndex)
    {
        var attachmentId = !string.IsNullOrEmpty(attachment.FileName)
            ? attachment.FileName
            : (!string.IsNullOrEmpty(attachment.DataBase64)
                ? attachment.DataBase64.Length.ToString()
                : null);

        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Attachment, sortIndex, attachmentId),
            Type = attachment.IsImage ? ContentBlockType.Image : ContentBlockType.Attachment,
            Attachment = attachment,
            SortIndex = sortIndex
        };
    }

    /// <summary>
    /// 创建错误内容块
    /// </summary>
    public static ContentBlock CreateError(string error, int sortIndex, string? errorId = null)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Error, sortIndex, errorId),
            Type = ContentBlockType.Error,
            Content = error,
            SortIndex = sortIndex,
            IsComplete = true
        };
    }

    /// <summary>
    /// 创建子代理内容块
    /// </summary>
    public static ContentBlock CreateSubAgent(string agentName, string? content, int sortIndex, bool isComplete = true)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.SubAgent, sortIndex, agentName),
            Type = ContentBlockType.SubAgent,
            Content = content,
            SortIndex = sortIndex,
            IsComplete = isComplete,
            IsStreaming = !isComplete,
            Extensions = new Dictionary<string, object>
            {
                ["subAgentName"] = agentName
            }
        };
    }

    /// <summary>
    /// 创建分隔线内容块
    /// </summary>
    public static ContentBlock CreateDivider(int stepIndex, int sortIndex)
    {
        return new ContentBlock
        {
            Id = GenerateId(ContentBlockType.Divider, sortIndex),
            Type = ContentBlockType.Divider,
            SortIndex = sortIndex,
            IsComplete = true,
            Extensions = new Dictionary<string, object>
            {
                ["stepIndex"] = stepIndex
            }
        };
    }
}

/// <summary>
/// Loop 内单条消息的内容块段（用于分段缓存）。
/// </summary>
public readonly record struct ContentBlockSegment(MessageViewModel Message, List<ContentBlock> Blocks);

/// <summary>
/// 消息内容块列表构建器 - 将消息转换为有序的内容块列表
/// </summary>
public class ContentBlockBuilder
{
    /// <summary>
    /// 从消息构建内容块列表
    /// 推理过程 -> 工具调用 -> 文本内容
    /// </summary>
    public static List<ContentBlock> BuildFromMessage(MessageViewModel message)
    {
        var blocks = BuildFromMessageCore(message);
        return blocks;
    }

    /// <summary>
    /// 从消息构建内容块，并为块 Id 加前缀以保证跨消息唯一。
    /// </summary>
    /// <param name="message">消息视图模型</param>
    /// <param name="idPrefix">块 Id 前缀（通常为消息 Id）；null 表示不加前缀</param>
    public static List<ContentBlock> BuildFromMessage(MessageViewModel message, string? idPrefix)
    {
        var blocks = BuildFromMessageCore(message);
        if (idPrefix is null)
            return blocks;

        for (var i = 0; i < blocks.Count; i++)
            blocks[i].Id = $"{idPrefix}:{blocks[i].Id}";

        return blocks;
    }

    /// <summary>
    /// 按 Step 顺序构建 Loop 的分段内容块。
    /// <para>块 Id 以消息 Id 为前缀，保证 Loop 内唯一且同一消息跨次构建稳定，
    /// 从而渲染层可按消息粒度缓存，避免每次 delta 重建整个 Loop。</para>
    /// <para>消息 Id 重复时（分叉复制/历史数据）追加 <c>#{n}</c> 后缀消歧；
    /// 序号按 Step 排序后的出现次序确定，因此同一输入每次构建结果一致。</para>
    /// </summary>
    public static List<ContentBlockSegment> BuildLoopSegments(IReadOnlyList<MessageViewModel> messages)
    {
        var ordered = messages.OrderBy(m => m.Step).ToList();
        var segments = new List<ContentBlockSegment>(ordered.Count);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < ordered.Count; i++)
        {
            var message = ordered[i];
            var prefix = string.IsNullOrEmpty(message.Id) ? $"anon-{i}" : message.Id;

            if (seen.TryGetValue(prefix, out var count))
            {
                seen[prefix] = count + 1;
                prefix = $"{prefix}#{count + 1}";
            }
            else
            {
                seen[prefix] = 0;
            }

            segments.Add(new ContentBlockSegment(message, BuildFromMessage(message, prefix)));
        }

        return segments;
    }

    /// <summary>构建内容块列表（无前缀版本的核心实现）。</summary>
    private static List<ContentBlock> BuildFromMessageCore(MessageViewModel message)
    {
        var blocks = new List<ContentBlock>();
        var index = 0;

        // 1. 先添加思考过程（如果有）
        if (!string.IsNullOrEmpty(message.Reasoning))
        {
            // 推理完成状态独立于消息整体完成状态：
            // 推理内容存在且没有正在流式更新 → 推理已完成
            var reasoningComplete = message.IsReasoningComplete || message.IsComplete;
            blocks.Add(ContentBlock.CreateReasoning(
                message.Reasoning,
                index++,
                reasoningComplete
            ));
        }

        // 2. 添加多模态内容段（按段类型分派，保留原始顺序）
        // 带附件的用户消息将文本存于 Parts 的 text 段（Content 为空），
        // 若不分派会把 text 段误当作附件，导致文本内容丢失。
        var hasTextPart = false;
        if (message.Parts is { Count: > 0 })
        {
            foreach (var part in message.Parts)
            {
                if (part.IsText)
                {
                    if (string.IsNullOrEmpty(part.Text))
                        continue;

                    hasTextPart = true;
                    blocks.Add(ContentBlock.CreateText(part.Text, index++, message.IsComplete));
                }
                else
                {
                    blocks.Add(ContentBlock.CreateAttachment(part, index++));
                }
            }
        }

        // 3. 添加工具调用（如果有）
        if (message.ToolCalls.Count > 0)
        {
            foreach (var toolCall in message.ToolCalls)
            {
                blocks.Add(ContentBlock.CreateToolCall(toolCall, index++));
            }
        }

        // 4. 添加文本内容（Parts 未提供文本段时使用，兼容旧的纯文本消息且避免重复）
        if (!hasTextPart && !string.IsNullOrEmpty(message.Content))
        {
            blocks.Add(ContentBlock.CreateText(
                message.Content,
                index++,
                message.IsComplete
            ));
        }

        // 5. 兜底：如果没有任何内容块，显示占位提示
        if (blocks.Count == 0)
        {
            blocks.Add(ContentBlock.CreateText(
                message.IsComplete ? "..." : "正在输入...",
                index++,
                message.IsComplete
            ));
        }

        return blocks;
    }
}
