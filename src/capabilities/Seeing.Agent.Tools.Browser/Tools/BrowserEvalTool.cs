using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_eval</c>：在页面上下文执行 JS 表达式并返回结果。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么它必须单独成一个工具</b>：它等价于「在目标页面上执行任意脚本」，能读取页面上任何数据
/// （含已登录后台的敏感信息）。合成进别的工具就等于给那些工具一并授了权，而权限判定不含参数，
/// 无法细分。单独拆出后可以要求每次审批。
/// </para>
/// <para>
/// <b>边界</b>：执行环境是页面（浏览器渲染进程），没有 Node / 文件系统访问；受同源策略约束。
/// </para>
/// </remarks>
public sealed class BrowserEvalTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserEvalTool(ILogger<BrowserEvalTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_eval";

    /// <inheritdoc />
    public override string Description =>
        "在网页里执行 JavaScript 表达式并返回结果（JSON），用于读取 DOM 细节或调用页面函数。\n\n" +
        "- expression 写成表达式或 IIFE，如 `document.title`、`(() => [...document.links].map(a => a.href))()`。\n" +
        "- 返回值为可序列化对象；undefined 归一化为 null。\n" +
        "- awaitPromise 默认 true：表达式返回 Promise 时会等待。\n" +
        "- 高危：能读取页面上任何数据。常规读取优先用 `browser_snapshot`，看画面用 `browser_screenshot`。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            expression = new
            {
                type = "string",
                description = "要执行的 JS 表达式或 IIFE，如 document.title 或 (() => document.body.innerText.slice(0, 500))()"
            },
            awaitPromise = new
            {
                type = "boolean",
                @default = true,
                description = "表达式返回 Promise 时是否等待（默认 true）"
            }
        },
        required = new[] { "expression" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var expression = BrowserJson.ReadString(arguments, "expression");
        if (string.IsNullOrWhiteSpace(expression))
            return Failure("缺少必需参数: expression");

        var awaitPromise = BrowserJson.ReadBool(arguments, "awaitPromise") ?? true;

        try
        {
            var result = await page.EvaluateAsync(expression, awaitPromise, context.CancellationToken)
                .ConfigureAwait(false);

            var rendered = result.ValueKind switch
            {
                JsonValueKind.Undefined or JsonValueKind.Null => "null",
                JsonValueKind.String => result.GetString() ?? string.Empty,
                _ => result.GetRawText()
            };

            return Success(
                rendered,
            context.SessionId,
                new Dictionary<string, object>
                {
                    ["url"] = page.CurrentUrl,
                    ["result_kind"] = result.ValueKind.ToString()
                });
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "执行脚本");
        }
    }
}
