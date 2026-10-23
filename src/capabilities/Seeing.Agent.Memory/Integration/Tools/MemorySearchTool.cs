using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Tools.Support;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Memory.Integration.Tools;

public sealed class MemorySearchTool : ToolBase
{
    private readonly IMemoryRecallService _recall;

    public MemorySearchTool(ILogger<MemorySearchTool> logger, IMemoryRecallService recall) : base(logger)
    {
        _recall = recall;
    }

    public override string Id => "memory_search";

    public override string Description =>
        "搜索长期记忆（daily、digest），返回相对路径与内容摘要。\n\n" +
        "- 结果：条数由记忆配置 TopK 决定（默认 5）。\n" +
        "- 取全文用 `memory_read`；不要用文件系统 `read` 打开记忆。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "查询文本" }
        },
        required = new[] { "query" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var query = GetStringArgument(arguments, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Failure("query 参数是必需的");

        try
        {
            var hits = await _recall.RecallAsync(query, context.CancellationToken);
            if (hits.Count == 0)
                return Success("未找到记忆");

            var sb = new StringBuilder();
            foreach (var h in hits)
            {
                var title = string.IsNullOrWhiteSpace(h.Node.Metadata.Title)
                    ? Path.GetFileNameWithoutExtension(h.Node.Path)
                    : h.Node.Metadata.Title!;
                var body = YamlParser.ExtractMarkdownBody(h.Node.Content).Trim();
                var snippet = Truncate(body, 280);
                sb.AppendLine($"- [{h.Score:0.00}] path={h.Node.Path}");
                sb.AppendLine($"  title: {title}");
                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine($"  content: {snippet}");
            }

            sb.AppendLine();
            sb.Append("如需完整内容，请对 path 调用 memory_read（不要用文件系统 read）。");
            return Success("记忆搜索", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text[..max].TrimEnd() + "…";
    }
}
