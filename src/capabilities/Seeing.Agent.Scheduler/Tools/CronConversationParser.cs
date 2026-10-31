using System.Text.Json;

namespace Seeing.Agent.Scheduler.Tools;

/// <summary>
/// 解析 cron 工具的出站 <c>conversation</c> 参数（可选）。
/// <para>
/// 与 <see cref="CronScheduleParser"/> 同样的宽容策略：字段名忽略大小写与下划线/连字符，
/// 并接受常见别名（<c>channel</c>、<c>chatId</c>、<c>name</c> 等）。
/// 只有 <c>channelId</c> / <c>kind</c> / <c>externalId</c> 齐备才生效（见 <c>ConversationSpec.IsComplete</c>）。
/// </para>
/// </summary>
internal static class CronConversationParser
{
    private const string ExamplesText =
        """
        示例：
        - {"channelId":"qq","kind":"group","externalId":"123456","title":"研发群"}
        kind 取值：direct（私聊）| group（群聊）| guild（频道）
        """;

    private static readonly string[] s_channelIdKeys = ["channelid", "channel"];
    private static readonly string[] s_kindKeys = ["kind", "conversationkind", "type"];
    private static readonly string[] s_externalIdKeys = ["externalid", "conversationid", "chatid", "groupid", "targetid"];
    private static readonly string[] s_titleKeys = ["title", "name", "displayname"];

    private static readonly string[] s_validKinds = ["direct", "group", "guild"];

    /// <summary>
    /// 解析 conversation 参数。
    /// <para>
    /// 未提供（null/undefined/空对象/空白字符串）时返回 true 且 <paramref name="conversation"/> 为 null，
    /// 表示「默认不填」：保存任务时由 <c>ScheduleManager</c> 从会话绑定自动快照。
    /// 只有部分字段的非空对象才视为无效（避免半截配置静默生效）。
    /// </para>
    /// </summary>
    public static bool TryParse(JsonElement element, out Models.ConversationSpec? conversation, out string? error)
    {
        conversation = null;
        error = null;

        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return true;

        // 空字符串同样视为「未填写」，避免模型用 "" 占位时整次创建失败
        if (element.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(element.GetString()))
            return true;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"无效的 conversation 参数：期望对象，实际收到 {element.ValueKind}。\n{ExamplesText}";
            return false;
        }

        var channelId = ReadString(element, s_channelIdKeys);
        var kind = ReadString(element, s_kindKeys)?.ToLowerInvariant();
        var externalId = ReadString(element, s_externalIdKeys);
        var title = ReadString(element, s_titleKeys);

        // 空对象（或全空字段）等价于「默认不填」，不当作错误
        if (string.IsNullOrWhiteSpace(channelId)
            && string.IsNullOrWhiteSpace(kind)
            && string.IsNullOrWhiteSpace(externalId)
            && string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(channelId)) missing.Add("channelId");
        if (string.IsNullOrWhiteSpace(kind)) missing.Add("kind");
        if (string.IsNullOrWhiteSpace(externalId)) missing.Add("externalId");

        if (missing.Count > 0)
        {
            error = $"conversation 需要 channelId、kind、externalId 齐备（缺少 {string.Join("、", missing)}）。\n{ExamplesText}";
            return false;
        }

        if (!s_validKinds.Contains(kind))
        {
            error = $"无效的 conversation.kind: '{kind}'，取值：direct | group | guild。\n{ExamplesText}";
            return false;
        }

        conversation = new Models.ConversationSpec
        {
            ChannelId = channelId!.Trim(),
            Kind = kind!,
            ExternalId = externalId!.Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim()
        };
        return true;
    }

    private static string? ReadString(JsonElement element, string[] keys)
    {
        foreach (var property in element.EnumerateObject())
        {
            var normalized = NormalizeKey(property.Name);
            foreach (var key in keys)
            {
                if (!string.Equals(normalized, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null
                };
            }
        }

        return null;
    }

    private static string NormalizeKey(string key) =>
        key.Replace("_", string.Empty).Replace("-", string.Empty).Trim();
}
