using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Tools;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 分页列出某通道已登记对话。
/// </summary>
public sealed class ListConversationsTool : ITool
{
    private readonly IChannelDirectory _directory;

    public ListConversationsTool(IChannelDirectory directory)
    {
        _directory = directory;
    }

    public string Id => "list_channel_conversations";

    public string Description => "分页列出通道目录中的对话。";

    public IReadOnlyList<string> Tags => [];

    public ToolCategory Category => ToolCategory.ExternalService;

    public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            channelId = new { type = "string", description = "通道 id" },
            skip = new { type = "integer", description = "跳过条数，默认 0" },
            take = new { type = "integer", description = "返回条数，默认 50" }
        },
        required = new[] { "channelId" }
    });

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var channelId = ToolJson.ReadString(arguments, "channelId");
        if (channelId == null)
        {
            return new ToolResult
            {
                Success = false,
                Title = "列出对话失败",
                Output = "缺少 channelId",
                Error = "缺少 channelId"
            };
        }

        var skip = ToolJson.ReadInt(arguments, "skip", 0);
        var take = ToolJson.ReadInt(arguments, "take", 50);
        var conversations = await _directory
            .ListConversationsAsync(channelId, skip, take, context.CancellationToken)
            .ConfigureAwait(false);

        var output = new StringBuilder();
        output.Append("conversations skip=").Append(skip).Append(" take=").Append(take).AppendLine();
        foreach (var entry in conversations)
        {
            var conversation = entry.Conversation;
            output.Append("- ").Append(conversation.Kind).Append(' ').Append(conversation.ExternalId);
            if (!string.IsNullOrWhiteSpace(conversation.Title))
                output.Append(" (").Append(conversation.Title).Append(')');
            output.AppendLine();
        }

        return new ToolResult
        {
            Success = true,
            Title = "通道对话",
            Output = output.ToString()
        };
    }
}
