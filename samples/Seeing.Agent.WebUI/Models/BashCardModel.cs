using Seeing.Agent.WebUI.Helpers;

namespace Seeing.Agent.WebUI.Models;

/// <summary>
/// bash 工具卡展示投影：从通用 <see cref="ToolCallViewModel"/> + Metadata 现算，不污染共享 VM。
/// </summary>
public sealed class BashCardModel
{
    public required ToolCallViewModel ToolCall { get; init; }
    public string? Command { get; init; }
    public string? Workdir { get; init; }
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Aborted { get; init; }
    /// <summary>参数/Metadata 回退标题；不回写 <see cref="ToolCallViewModel.Description"/>。</summary>
    public string? TitleOverride { get; init; }
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(TitleOverride) ? TitleOverride!
        : !string.IsNullOrWhiteSpace(ToolCall.Description) ? ToolCall.Description!
        : "bash";
    public string DisplayOutput => BashToolDisplayHelper.StripBashMetadataBlock(ToolCall.Result);

    public static BashCardModel From(ToolCallViewModel toolCall)
    {
        string? command = null;
        string? workdir = null;
        string? descriptionFallback = toolCall.Description;

        if (!string.IsNullOrWhiteSpace(toolCall.Parameters))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(toolCall.Parameters);
                var root = doc.RootElement;
                if (root.TryGetProperty("command", out var cmd) && cmd.ValueKind == System.Text.Json.JsonValueKind.String)
                    command = cmd.GetString();
                if (string.IsNullOrEmpty(descriptionFallback) &&
                    root.TryGetProperty("description", out var desc) &&
                    desc.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    descriptionFallback = desc.GetString();
                }
                if (root.TryGetProperty("workdir", out var wd) && wd.ValueKind == System.Text.Json.JsonValueKind.String)
                    workdir = wd.GetString();
            }
            catch
            {
                // ignore
            }
        }

        int? exit = null;
        var timedOut = false;
        var aborted = false;
        var metadata = toolCall.Metadata;
        if (metadata != null)
        {
            if (TryGetInt(metadata, "exit", out var e))
                exit = e;
            if (TryGetBool(metadata, "timedOut", out var t))
                timedOut = t;
            if (TryGetBool(metadata, "aborted", out var a))
                aborted = a;
            if (string.IsNullOrEmpty(descriptionFallback) &&
                metadata.TryGetValue("description", out var d) && d != null)
            {
                descriptionFallback = d.ToString();
            }
        }

        return new BashCardModel
        {
            ToolCall = toolCall,
            Command = command,
            Workdir = workdir,
            ExitCode = exit,
            TimedOut = timedOut,
            Aborted = aborted,
            TitleOverride = descriptionFallback
        };
    }

    private static bool TryGetInt(Dictionary<string, object> metadata, string key, out int value)
    {
        value = 0;
        if (!metadata.TryGetValue(key, out var raw) || raw == null)
            return false;
        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l:
                value = (int)l;
                return true;
            case System.Text.Json.JsonElement je when je.TryGetInt32(out var ji):
                value = ji;
                return true;
            default:
                return int.TryParse(raw.ToString(), out value);
        }
    }

    private static bool TryGetBool(Dictionary<string, object> metadata, string key, out bool value)
    {
        value = false;
        if (!metadata.TryGetValue(key, out var raw) || raw == null)
            return false;
        switch (raw)
        {
            case bool b:
                value = b;
                return true;
            case System.Text.Json.JsonElement je when je.ValueKind is System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False:
                value = je.GetBoolean();
                return true;
            default:
                return bool.TryParse(raw.ToString(), out value);
        }
    }
}
