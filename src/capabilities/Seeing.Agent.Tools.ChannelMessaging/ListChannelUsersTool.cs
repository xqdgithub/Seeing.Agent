using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Tools;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 分页列出某通道已登记用户。
/// </summary>
public sealed class ListChannelUsersTool : ITool
{
    private readonly IChannelDirectory _directory;

    public ListChannelUsersTool(IChannelDirectory directory)
    {
        _directory = directory;
    }

    public string Id => "list_channel_users";

    public string Description => "分页列出通道目录中的用户。";

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
                Title = "列出用户失败",
                Output = "缺少 channelId",
                Error = "缺少 channelId"
            };
        }

        var skip = ToolJson.ReadInt(arguments, "skip", 0);
        var take = ToolJson.ReadInt(arguments, "take", 50);
        var users = await _directory.ListUsersAsync(channelId, skip, take, context.CancellationToken)
            .ConfigureAwait(false);

        var output = new StringBuilder();
        output.Append("users skip=").Append(skip).Append(" take=").Append(take).AppendLine();
        foreach (var user in users)
        {
            output.Append("- ").Append(user.ExternalId);
            if (!string.IsNullOrWhiteSpace(user.DisplayName))
                output.Append(" (").Append(user.DisplayName).Append(')');
            output.Append(" optedOut=").Append(user.OptedOut).AppendLine();
        }

        return new ToolResult
        {
            Success = true,
            Title = "通道用户",
            Output = output.ToString()
        };
    }
}
