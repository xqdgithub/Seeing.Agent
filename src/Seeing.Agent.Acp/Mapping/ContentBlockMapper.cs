using Acp.Types;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Acp.Mapping;

/// <summary>
/// ChatMessage / 用户输入 → ACP ContentBlock。
/// </summary>
public sealed class ContentBlockMapper
{
    /// <summary>
    /// Passthrough：仅映射本轮用户 delta（最后一条 user 消息）。
    /// </summary>
    public IEnumerable<ContentBlock> MapUserDelta(AgentContext context)
    {
        var lastUser = context.History.LastOrDefault(m => m.Role == "user");
        if (lastUser == null)
            return new ContentBlock[] { new TextContentBlock("Hello") };

        return MapMessage(lastUser);
    }

    public IEnumerable<ContentBlock> MapPrompt(string prompt) =>
        new ContentBlock[] { new TextContentBlock(prompt) };

    public IEnumerable<ContentBlock> MapMessage(ChatMessage message)
    {
        var blocks = new List<ContentBlock>();

        if (!string.IsNullOrWhiteSpace(message.Content))
            blocks.Add(new TextContentBlock(message.Content));

        foreach (var part in message.GetEffectiveParts())
        {
            switch (part.Type)
            {
                case ChatContentPart.KindText when !string.IsNullOrWhiteSpace(part.Text):
                    blocks.Add(new TextContentBlock(part.Text));
                    break;
                case ChatContentPart.KindImage when !string.IsNullOrWhiteSpace(part.DataBase64):
                    blocks.Add(new ImageContentBlock
                    {
                        MimeType = part.MimeType,
                        Data = part.DataBase64
                    });
                    break;
                case ChatContentPart.KindFile when !string.IsNullOrWhiteSpace(part.FileName):
                    blocks.Add(new ResourceLinkContentBlock
                    {
                        Uri = part.FileName,
                        Name = part.FileName,
                        MimeType = part.MimeType
                    });
                    break;
            }
        }

        if (blocks.Count == 0)
            blocks.Add(new TextContentBlock(""));

        return blocks;
    }
}
