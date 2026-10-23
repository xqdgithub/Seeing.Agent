using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Tools.ChannelMessaging;

/// <summary>
/// 向已登记的用户或对话投递一条文本与工作区附件。
/// <para>所有依赖均为必需：缺失即 DI 失败，禁止「依赖缺失就跳过校验」。</para>
/// </summary>
public sealed class SendMessageTool : ITool
{
    private const string ParametersSchemaJson = """
    {
      "type": "object",
      "properties": {
        "channelId": {
          "type": "string",
          "description": "通道 id，如 qq。省略时取当前会话绑定的通道（含父会话回溯）；显式目标也允许省略，解析不到时返回目标解析失败（未绑定通道或通道未配置）。不要只给 channelId"
        },
        "targetUserId": {
          "type": "string",
          "description": "目标用户的 ExternalId。与 conversationExternalId/conversationKind 二选一；都省略时发到当前会话绑定的对话"
        },
        "conversationExternalId": {
          "type": "string",
          "description": "目标对话的 ExternalId。必须与 conversationKind 同时提供"
        },
        "conversationKind": {
          "type": "string",
          "enum": ["direct", "group", "guild"],
          "description": "对话种类：direct 私聊、group 群、guild 频道"
        },
        "text": {
          "type": "string",
          "description": "消息正文。与 attachments 至少提供一个"
        },
        "attachments": {
          "type": "array",
          "description": "附件列表。每项必须是工作区内的文件；类型由 kind 或扩展名判定，数量与大小上限由目标通道能力决定",
          "items": {
            "type": "object",
            "description": "单个附件：本地路径，以及可选的类型与展示名",
            "properties": {
              "path": { "type": "string", "description": "绝对路径，或相对当前工作目录的路径；必须位于工作区内" },
              "kind": {
                "type": "string",
                "enum": ["image", "video", "audio", "file"],
                "description": "附件类型：image 图片、video 视频、audio 音频、file 其他文件；省略时按扩展名推断"
              },
              "name": { "type": "string", "description": "展示文件名；省略时取路径的文件名" }
            },
            "required": ["path"]
          }
        }
      },
      "required": []
    }
    """;

    private static readonly JsonElement s_parametersSchema =
        JsonDocument.Parse(ParametersSchemaJson).RootElement.Clone();

    private readonly IChannelMessageSink _sink;
    private readonly IChannelTargetResolver _resolver;
    private readonly ChannelAttachmentNormalizer _normalizer;

    public SendMessageTool(
        IChannelMessageSink sink,
        IChannelTargetResolver resolver,
        IWorkspacePathGate pathGate,
        ISessionWorkspaceResolver sessionWorkspace,
        IWorkspaceProvider workspace,
        IFileSystem fileSystem)
    {
        _sink = sink;
        _resolver = resolver;
        _normalizer = new ChannelAttachmentNormalizer(pathGate, sessionWorkspace, workspace, fileSystem);
    }

    public string Id => "send_channel_message";

    public string Description =>
        "向已登记的通道对话发送文本和文件。\n" +
        "- 目标三选一：targetUserId；conversationExternalId + conversationKind；都省略时发到当前会话绑定的对话。\n" +
        "- 只给 channelId 而不给目标会报参数错误；要发到当前会话就省略 channelId。\n" +
        "- 附件只接受工作区内的文件；path 写绝对路径，或相对当前工作目录的路径。\n" +
        "- 支持的类型、单文件上限与单条数量由目标通道能力决定，超出时返回明确失败原因。\n" +
        "- 查目标用 `list_channel_users` / `list_channel_conversations`。";

    public IReadOnlyList<string> Tags => [];

    public ToolCategory Category => ToolCategory.ExternalService;

    public JsonElement ParametersSchema => s_parametersSchema;

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var channelId = ToolJson.ReadString(arguments, "channelId");
        var targetUserId = ToolJson.ReadString(arguments, "targetUserId");
        var conversationExternalId = ToolJson.ReadString(arguments, "conversationExternalId");
        var conversationKindText = ToolJson.ReadString(arguments, "conversationKind");
        var text = ToolJson.ReadString(arguments, "text");
        var attachments = ReadAttachments(arguments);

        var hasUser = targetUserId != null;
        var hasConversation = conversationExternalId != null || conversationKindText != null;

        // ── 参数自洽 ──
        if (hasUser && hasConversation)
            return ParameterError("targetUserId 与 conversationExternalId+conversationKind 必须二选一");

        if (hasConversation && (conversationExternalId == null || conversationKindText == null))
            return ParameterError("conversationExternalId 与 conversationKind（direct|group|guild）必须同时提供");

        var conversationKind = ConversationKind.Direct;
        if (hasConversation && !ToolJson.TryParseKind(conversationKindText!, out conversationKind))
            return ParameterError("conversationKind 必须是 direct、group 或 guild");

        if (!hasUser && !hasConversation && channelId != null)
            return ParameterError(
                "已指定 channelId 但未指定目标对话；请给出 targetUserId / conversationExternalId+conversationKind，" +
                "或省略 channelId 以发送到当前会话");

        if (text == null && attachments.Count == 0)
            return ParameterError("text 与 attachments 至少提供一个");

        // ── 附件归一化（工作区边界 + 文件事实）──
        var normalizedAttachments = new List<OutboundAttachment>(attachments.Count);
        foreach (var attachment in attachments)
        {
            var normalized = _normalizer.Normalize(context.SessionId, attachment);
            if (normalized.IsParameterError)
                return ParameterError(normalized.Error!);
            if (normalized.Attachment is null)
                return DeliveryFailure(OutboundFailureKind.AttachmentUnavailable, normalized.Error);
            normalizedAttachments.Add(normalized.Attachment);
        }

        var content = new OutboundContent
        {
            Text = text,
            Attachments = normalizedAttachments
        };

        var ct = context.CancellationToken;

        // ── 目标全空：回落到当前会话绑定的完整目标 ──
        if (!hasUser && !hasConversation)
        {
            var resolution = await _resolver
                .ResolveSessionTargetAsync(context.SessionId, ct)
                .ConfigureAwait(false);
            if (resolution.Target is null)
                return DeliveryFailure(resolution.FailureKind ?? OutboundFailureKind.NotInDirectory, resolution.Detail);

            return await SendToConversationAsync(resolution.Target, content, ct).ConfigureAwait(false);
        }

        // ── 显式目标：channelId 显式值优先，否则只向 resolver 取 Conversation.ChannelId ──
        var resolvedChannelId = channelId;
        if (resolvedChannelId == null)
        {
            var resolution = await _resolver
                .ResolveSessionTargetAsync(context.SessionId, ct)
                .ConfigureAwait(false);
            if (resolution.Target is null)
            {
                var detail = string.IsNullOrWhiteSpace(resolution.Detail)
                    ? "当前会话及父会话均未绑定通道"
                    : resolution.Detail!;
                return DeliveryFailure(
                    resolution.FailureKind ?? OutboundFailureKind.NotInDirectory,
                    $"{detail}；请提供 channelId");
            }

            resolvedChannelId = resolution.Target.Conversation.ChannelId;
        }

        if (hasUser)
            return await SendToUserAsync(resolvedChannelId, targetUserId!, content, ct).ConfigureAwait(false);

        var target = new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = resolvedChannelId,
                Kind = conversationKind,
                ExternalId = conversationExternalId!
            }
        };
        return await SendToConversationAsync(target, content, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> SendToUserAsync(
        string channelId, string userExternalId, OutboundContent content, CancellationToken ct)
    {
        var result = await _sink
            .SendToUserAsync(channelId, userExternalId, content, ct)
            .ConfigureAwait(false);

        if (!result.Delivered)
            return DeliveryFailure(result.FailureKind ?? OutboundFailureKind.PlatformError, result.Detail);

        var target = new DeliveryTarget
        {
            Conversation = new ConversationInfo
            {
                ChannelId = channelId,
                Kind = ConversationKind.Direct,
                ExternalId = userExternalId
            },
            RecipientUserId = userExternalId
        };
        return Succeeded(target, content);
    }

    private async Task<ToolResult> SendToConversationAsync(
        DeliveryTarget target, OutboundContent content, CancellationToken ct)
    {
        var result = await _sink
            .SendToConversationAsync(target, content, ct)
            .ConfigureAwait(false);

        return result.Delivered
            ? Succeeded(target, content)
            : DeliveryFailure(result.FailureKind ?? OutboundFailureKind.PlatformError, result.Detail);
    }

    private static ToolResult Succeeded(DeliveryTarget target, OutboundContent content)
    {
        var output = new StringBuilder(ChannelDeliveryMessages.Delivered);
        output.Append('：')
            .Append(target.Conversation.ChannelId)
            .Append(' ')
            .Append(KindLabel(target.Conversation.Kind))
            .Append(' ')
            .Append(target.Conversation.ExternalId);

        if (content.Attachments.Count > 0)
        {
            output.Append("；附件：")
                .Append(string.Join("、", content.Attachments.Select(a => a.FileName ?? a.LocalPath)));
        }

        return new ToolResult
        {
            Success = true,
            Title = ChannelDeliveryMessages.Delivered,
            Output = output.ToString()
        };
    }

    private static string KindLabel(ConversationKind kind) => kind switch
    {
        ConversationKind.Direct => "私聊",
        ConversationKind.Group => "群",
        _ => "频道"
    };

    private static ToolResult ParameterError(string message) => new()
    {
        Success = false,
        Title = "参数错误",
        Output = message,
        Error = message
    };

    private static ToolResult DeliveryFailure(OutboundFailureKind kind, string? detail)
    {
        var message = ChannelDeliveryMessages.For(OutboundDeliveryResult.Fail(kind, detail));
        return new ToolResult
        {
            Success = false,
            Title = "投递失败",
            Output = message,
            Error = message
        };
    }

    private static List<JsonElement> ReadAttachments(JsonElement arguments)
    {
        var items = new List<JsonElement>();
        if (!arguments.TryGetProperty("attachments", out var property)
            || property.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var item in property.EnumerateArray())
            items.Add(item);

        return items;
    }
}
