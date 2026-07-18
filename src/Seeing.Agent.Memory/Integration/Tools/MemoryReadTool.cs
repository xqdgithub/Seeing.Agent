using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;

namespace Seeing.Agent.Memory.Integration.Tools;

/// <summary>
/// 按记忆相对路径读取完整内容（勿用文件系统 read 猜绝对路径）。
/// </summary>
public sealed class MemoryReadTool : ToolBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public MemoryReadTool(
        ILogger<MemoryReadTool> logger,
        IServiceScopeFactory scopeFactory) : base(logger)
    {
        _scopeFactory = scopeFactory;
    }

    public override string Id => "memory_read";

    public override string Description =>
        "读取一条长期记忆的完整内容。path 为 memory_search 返回的相对路径（如 daily/2026-07-18/xxx.md）。不要用文件系统 read，也不要猜测 ~/.agents 等绝对路径。";

    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type = "string",
                description = "记忆相对路径，例如 daily/2026-07-18/explicit-xxx.md"
            }
        },
        required = new[] { "path" }
    });

    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var path = NormalizePath(GetStringArgument(arguments, "path"));
        if (string.IsNullOrWhiteSpace(path))
            return Failure("path 参数是必需的");

        if (path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
            return Failure("path 必须是相对路径（如 daily/...），不能是绝对路径");

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var node = await memory.GetAsync(path, context.CancellationToken);
            if (node is null)
                return Failure($"未找到记忆: {path}");

            var title = string.IsNullOrWhiteSpace(node.Metadata.Title) ? path : node.Metadata.Title!;
            var body = YamlParser.ExtractMarkdownBody(node.Content).Trim();
            var output = $"""
                path: {node.Path}
                title: {title}
                importance: {node.Metadata.Importance:0.###}
                tags: [{string.Join(", ", node.Metadata.Tags)}]

                {body}
                """;
            return Success("记忆内容", output);
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Trim().Replace('\\', '/');
        // 模型有时会拼出绝对路径或错误根目录，尽量剥到 daily|digest|session 起
        foreach (var marker in new[] { "/daily/", "/digest/", "/session/" })
        {
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return path[(idx + 1)..];
        }

        if (path.StartsWith("daily/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("digest/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("session/", StringComparison.OrdinalIgnoreCase))
            return path;

        return path;
    }
}
