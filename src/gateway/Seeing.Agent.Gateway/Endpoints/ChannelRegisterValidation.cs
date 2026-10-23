using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Gateway.Endpoints;

/// <summary>
/// 注册帧信任边界：<c>required</c> 只保证字段出现、不保证非 null，
/// 这里只做 null 结构校验，拒绝残缺能力（不引入新类型）。
/// </summary>
internal static class ChannelRegisterValidation
{
    /// <summary>
    /// 能力结构是否可用于注册：能力本身、默认附件能力的 <c>Kinds</c>、
    /// 按对话种类覆盖条目的 <c>Capabilities</c> 与 <c>Kinds</c> 均不得为 null。
    /// </summary>
    public static bool IsValid(ChannelCapabilities? capabilities)
    {
        if (capabilities is null)
            return false;

        if (capabilities.Attachments is { Kinds: null })
            return false;

        if (capabilities.AttachmentsByConversationKind is { } overrides)
        {
            foreach (var entry in overrides)
            {
                if (entry is null || entry.Capabilities is null || entry.Capabilities.Kinds is null)
                    return false;
            }
        }

        return true;
    }
}
