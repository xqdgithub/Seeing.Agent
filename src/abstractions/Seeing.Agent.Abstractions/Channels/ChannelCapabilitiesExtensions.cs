namespace Seeing.Agent.Abstractions.Channels;

/// <summary>渠道能力的派生查询。DTO 保持纯数据，派生语义集中在此纯函数扩展。</summary>
public static class ChannelCapabilitiesExtensions
{
    /// <summary>该附件能力是否接受指定附件类型。</summary>
    public static bool Supports(this ChannelAttachmentCapabilities capabilities, OutboundAttachmentKind kind)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.Kinds.Contains(kind);
    }

    /// <summary>按对话种类解析附件能力；通道不支持附件时返回 null。</summary>
    public static ChannelAttachmentCapabilities? ResolveAttachments(
        this ChannelCapabilities capabilities, ConversationKind kind)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // 有序条目线性取首个命中；重复 Kind 不抛错（条目数 ≤3，不值得加构造校验）
        if (capabilities.AttachmentsByConversationKind is { } overrides)
        {
            foreach (var entry in overrides)
            {
                if (entry.Kind == kind)
                    return entry.Capabilities;
            }
        }

        return capabilities.Attachments;
    }
}
