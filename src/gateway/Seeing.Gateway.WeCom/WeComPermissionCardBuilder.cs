using System.Text.Json.Serialization;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 构建企微 button_interaction 权限确认模板卡片
/// </summary>
public static class WeComPermissionCardBuilder
{
    public static WeComTemplateCardRespondBody BuildPromptCard(
        string taskId,
        string title,
        string description,
        string resource)
    {
        return new WeComTemplateCardRespondBody
        {
            TemplateCard = new WeComTemplateCardPayload
            {
                CardType = "button_interaction",
                TaskId = taskId,
                MainTitle = new WeComTemplateCardTitle { Title = title },
                SubTitleText = Truncate(description, 200),
                HorizontalContentList = string.IsNullOrWhiteSpace(resource)
                    ? null
                    :
                    [
                        new WeComTemplateCardHorizontalContent
                        {
                            KeyName = "资源",
                            Value = Truncate(resource, 100)
                        }
                    ],
                ButtonList =
                [
                    new WeComTemplateCardButton { Text = "批准", Style = 1, Key = "allow" },
                    new WeComTemplateCardButton { Text = "拒绝", Style = 2, Key = "deny" }
                ]
            }
        };
    }

    public static WeComTemplateCardUpdateBody BuildResultCard(string taskId, bool allowed, string? reason = null)
    {
        var title = allowed ? "已批准" : "已拒绝";
        var subtitle = string.IsNullOrWhiteSpace(reason)
            ? (allowed ? "操作已继续执行" : "操作已取消")
            : reason;

        return new WeComTemplateCardUpdateBody
        {
            TemplateCard = new WeComTemplateCardPayload
            {
                CardType = "button_interaction",
                TaskId = taskId,
                MainTitle = new WeComTemplateCardTitle { Title = title },
                SubTitleText = Truncate(subtitle, 200)
            }
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength] + "...";
    }
}

public sealed class WeComTemplateCardRespondBody
{
    [JsonPropertyName("msgtype")]
    public string MsgType { get; init; } = "template_card";

    [JsonPropertyName("template_card")]
    public required WeComTemplateCardPayload TemplateCard { get; init; }
}

public sealed class WeComTemplateCardUpdateBody
{
    [JsonPropertyName("response_type")]
    public string ResponseType { get; init; } = "update_template_card";

    [JsonPropertyName("template_card")]
    public required WeComTemplateCardPayload TemplateCard { get; init; }
}

public sealed class WeComTemplateCardPayload
{
    [JsonPropertyName("card_type")]
    public required string CardType { get; init; }

    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("main_title")]
    public WeComTemplateCardTitle? MainTitle { get; init; }

    [JsonPropertyName("sub_title_text")]
    public string? SubTitleText { get; init; }

    [JsonPropertyName("horizontal_content_list")]
    public List<WeComTemplateCardHorizontalContent>? HorizontalContentList { get; init; }

    [JsonPropertyName("button_list")]
    public List<WeComTemplateCardButton>? ButtonList { get; init; }
}

public sealed class WeComTemplateCardTitle
{
    [JsonPropertyName("title")]
    public required string Title { get; init; }
}

public sealed class WeComTemplateCardHorizontalContent
{
    [JsonPropertyName("keyname")]
    public required string KeyName { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed class WeComTemplateCardButton
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("style")]
    public int Style { get; init; }

    [JsonPropertyName("key")]
    public required string Key { get; init; }
}
