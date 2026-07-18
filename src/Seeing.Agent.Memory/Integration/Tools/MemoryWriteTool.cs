using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Memory.Abstractions;

namespace Seeing.Agent.Memory.Integration.Tools;

/// <summary>
/// 主动写入长期记忆。importance=1.0，直接落盘并建索引，不经启发式过滤/抽取管线。
/// </summary>
public sealed class MemoryWriteTool : ToolBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public MemoryWriteTool(
        ILogger<MemoryWriteTool> logger,
        IServiceScopeFactory scopeFactory) : base(logger)
    {
        _scopeFactory = scopeFactory;
    }

    public override string Id => "memory_write";

    public override string Description =>
        "将重要信息写入长期记忆（最高优先级，立即持久化并可被检索）。用于用户明确要求记住、偏好、决策、关键事实等。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string", description = "记忆标题（简短）" },
            content = new { type = "string", description = "要记住的完整内容" },
            tags = new
            {
                type = "array",
                items = new { type = "string" },
                description = "可选标签"
            }
        },
        required = new[] { "title", "content" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var title = GetStringArgument(arguments, "title")?.Trim();
        var content = GetStringArgument(arguments, "content")?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return Failure("title 参数是必需的");
        if (string.IsNullOrWhiteSpace(content))
            return Failure("content 参数是必需的");

        var tags = ParseTags(arguments);
        var id = $"explicit-{Guid.NewGuid():N}";
        var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        var path = $"daily/{date}/{id}.md";
        var sessionId = string.IsNullOrWhiteSpace(context.SessionId) ? "" : context.SessionId;
        var body = BuildDocument(id, title, content, tags, sessionId);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var node = await memory.SaveAsync(path, body, context.CancellationToken);
            return Success("记忆已写入", $"已保存（importance=1.0）: {node.Path}");
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    public static string BuildDocument(
        string id,
        string title,
        string content,
        IReadOnlyList<string> tags,
        string sessionId)
    {
        var tagsYaml = string.Join(", ", tags.Select(t => t));
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {id}");
        sb.AppendLine("type: daily");
        sb.AppendLine($"title: \"{EscapeYaml(title)}\"");
        sb.AppendLine($"tags: [{tagsYaml}]");
        sb.AppendLine("importance: 1.0");
        sb.AppendLine("kind: explicit");
        sb.AppendLine("source: tool");
        if (!string.IsNullOrEmpty(sessionId))
            sb.AppendLine($"source_session: {sessionId}");
        sb.AppendLine($"created_at: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(content);
        if (!content.EndsWith('\n'))
            sb.AppendLine();
        return sb.ToString();
    }

    private static List<string> ParseTags(JsonElement arguments)
    {
        var tags = new List<string>();
        if (!arguments.TryGetProperty("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
            return tags;

        foreach (var item in tagsEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var t = item.GetString()?.Trim();
                if (!string.IsNullOrEmpty(t))
                    tags.Add(t);
            }
        }

        return tags;
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
