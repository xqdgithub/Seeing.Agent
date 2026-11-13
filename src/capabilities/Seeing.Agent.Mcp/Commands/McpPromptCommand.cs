using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Runtime;

namespace Seeing.Agent.Mcp.Commands;

/// <summary>
/// 一条 MCP Prompt 对应一个斜杠命令。收齐参数 → <c>prompts/get</c> →
/// <see cref="CommandResult.ShouldContinue"/> = false 后只投递一条 <see cref="InboxDelivery.Wake"/> 用户消息。
/// </summary>
internal sealed class McpPromptCommand : ICommand
{
    private readonly string _serverName;
    private readonly string _promptName;
    private readonly McpPromptInfo _prompt;
    private readonly Func<string, IMcpSession?> _resolveSession;

    public McpPromptCommand(
        string serverName,
        McpPromptInfo prompt,
        Func<string, IMcpSession?> resolveSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt.Name);
        _serverName = serverName;
        _promptName = prompt.Name;
        _prompt = prompt;
        _resolveSession = resolveSession ?? throw new ArgumentNullException(nameof(resolveSession));

        var name = McpPromptCommandNames.Build(serverName, prompt.Name);
        Metadata = new CommandMetadata
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(prompt.Description)
                ? $"MCP prompt: {serverName}/{prompt.Name}"
                : prompt.Description!,
            Usage = BuildUsage(name, prompt),
            Category = CommandCategory.Tools,
            Type = CommandType.Default,
            Source = "mcp",
            Examples = [$"/{name}"],
        };
    }

    /// <inheritdoc />
    public CommandMetadata Metadata { get; }

    /// <summary>本命令所属 MCP server（同步器撤销时按 server 过滤）。</summary>
    public string ServerName => _serverName;

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        CancellationToken cancellationToken = default)
    {
        var session = _resolveSession(_serverName);
        if (session is null)
            return CommandResult.Fail($"MCP server '{_serverName}' 未连接或无会话");

        IReadOnlyDictionary<string, object?>? arguments;
        try
        {
            arguments = ParseArguments(context.Arguments, _prompt.Arguments);
        }
        catch (ArgumentException ex)
        {
            return CommandResult.Fail(ex.Message, $"用法: {Metadata.Usage}");
        }

        var get = await session.GetPromptAsync(_promptName, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (get.NotSupported)
            return CommandResult.Fail(get.ErrorMessage ?? "server 未声明 prompts 能力");
        if (!string.IsNullOrEmpty(get.ErrorMessage) && get.Messages.Count == 0)
            return CommandResult.Fail(get.ErrorMessage);

        var body = JoinPromptMessages(get.Messages);
        if (string.IsNullOrWhiteSpace(body))
            return CommandResult.Fail("MCP prompt 返回空内容");

        var runtime = context.Services?.GetService<ISessionRuntime>();
        if (runtime is null)
            return CommandResult.Fail("宿主未注册 ISessionRuntime，无法投递 prompt 消息");

        if (string.IsNullOrWhiteSpace(context.SessionId))
            return CommandResult.Fail("缺少 SessionId，无法投递 prompt 消息");

        var deliver = await runtime.DeliverAsync(new InboxMessage
        {
            TargetSessionId = context.SessionId,
            Sender = new InboxSender { Kind = InboxSenderKind.Human },
            Body = body,
            Delivery = InboxDelivery.Wake,
            Presentation = InboxPresentation.User,
            SourceTag = "mcp.prompt",
        }, cancellationToken).ConfigureAwait(false);

        if (!deliver.Success)
            return CommandResult.Fail(deliver.Error ?? "投递 MCP prompt 失败");

        // Spec §7：结束本轮（不 ShouldContinue），再只投递一条 Wake——禁止 AddMessage / 双投递。
        return new CommandResult
        {
            Success = true,
            Message = "已展开 MCP prompt",
            RemoveCommandMessage = true,
            ShouldContinue = false,
        };
    }

    internal static string JoinPromptMessages(IReadOnlyList<McpPromptMessage> messages)
    {
        if (messages.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < messages.Count; i++)
        {
            if (i > 0)
                sb.Append("\n\n");

            var msg = messages[i];
            if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("[assistant]\n");
                sb.Append(msg.Text);
            }
            else
            {
                sb.Append(msg.Text);
            }
        }

        return sb.ToString();
    }

    private static string BuildUsage(string name, McpPromptInfo prompt)
    {
        var args = prompt.Arguments;
        if (args is null || args.Count == 0)
            return $"/{name}";

        var parts = args.Select(a => a.Required ? $"<{a.Name}>" : $"[{a.Name}]");
        return $"/{name} " + string.Join(' ', parts);
    }

    /// <summary>
    /// 参数解析：JSON 对象，或 <c>key=value</c> 空格分隔；无参数声明时透传空表。
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? ParseArguments(
        string? raw,
        IReadOnlyList<McpPromptArgumentInfo>? declared)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (declared is null)
                return null;

            var missing = declared.Where(a => a.Required).Select(a => a.Name).ToList();
            if (missing.Count > 0)
                throw new ArgumentException($"缺少必填参数: {string.Join(", ", missing)}");
            return null;
        }

        if (trimmed.StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("参数 JSON 必须是对象");

            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
                map[prop.Name] = ConvertJson(prop.Value);
            EnsureRequired(map, declared);
            return map;
        }

        var fromPairs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0)
                throw new ArgumentException($"无法解析参数 '{token}'，请使用 key=value 或 JSON 对象");
            fromPairs[token[..eq]] = token[(eq + 1)..];
        }

        EnsureRequired(fromPairs, declared);
        return fromPairs;
    }

    private static void EnsureRequired(
        IReadOnlyDictionary<string, object?> map,
        IReadOnlyList<McpPromptArgumentInfo>? declared)
    {
        if (declared is null)
            return;

        var missing = declared
            .Where(a => a.Required && !map.ContainsKey(a.Name))
            .Select(a => a.Name)
            .ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"缺少必填参数: {string.Join(", ", missing)}");
    }

    private static object? ConvertJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };
}
