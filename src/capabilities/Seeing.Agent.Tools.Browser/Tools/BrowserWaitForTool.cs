using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_wait_for</c>：等待元素出现、文本出现、网络空闲或固定时长。
/// </summary>
/// <remarks>
/// 页面加载完不代表内容就绪（前端渲染、接口回填）。不等待就去快照，会拿到空列表并让 agent
/// 误判「页面上没有这个按钮」。这个工具是那种误判的解药。
/// </remarks>
[ToolCapability(ToolCapabilityKeys.TimeoutSkip, "true")]
public sealed class BrowserWaitForTool : BrowserToolBase
{
    /// <summary>创建工具。</summary>
    public BrowserWaitForTool(ILogger<BrowserWaitForTool> logger, IBrowserSessionHost host)
        : base(logger, host)
    {
    }

    /// <inheritdoc />
    public override string Id => "browser_wait_for";

    /// <inheritdoc />
    public override string Description =>
        "等待页面条件成立：元素出现、文本出现、网络空闲或固定时长。\n\n" +
        "- kind=selector 需给 value（CSS 选择器）；kind=text 需给 value（页面可见文本片段）。\n" +
        "- kind=ref 等待某个快照 ref 对应的元素出现。\n" +
        "- kind=networkidle 等网络空闲（无未完成请求约 0.5 秒）。真实站点可能永远不空闲，此时到预算会按就绪返回并注明。\n" +
        "- kind=timeout 固定等待 duration 秒（缺省等于 timeout）；timeout 始终是整次等待的上限。\n" +
        "- 只读，不改变页面；超时会明确说明等的是什么。";

    /// <inheritdoc />
    public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            kind = new
            {
                type = "string",
                @enum = new[] { "selector", "ref", "text", "networkidle", "timeout" },
                description = "等待条件：selector（元素出现）、ref（快照 ref 出现）、text（文本出现）、networkidle（网络空闲）、timeout（固定等待）"
            },
            value = new
            {
                type = "string",
                description = "条件值：kind=selector 时为 CSS 选择器，kind=ref 时为 ref，kind=text 时为文本片段"
            },
            timeout = new
            {
                type = "integer",
                minimum = 1,
                maximum = 300,
                description = "最长等待秒数（默认 30）。对所有 kind 都是上限；kind=timeout 时是等待时长的上限"
            },
            duration = new
            {
                type = "integer",
                minimum = 1,
                maximum = 300,
                description = "仅 kind=timeout：固定等待秒数（缺省等于 timeout，且不得超过 timeout）"
            }
        },
        required = new[] { "kind" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var kindText = BrowserJson.ReadString(arguments, "kind");
        var kind = BrowserJson.ReadEnum<BrowserWaitKind>(arguments, "kind");
        if (kind is null)
        {
            return Failure(string.IsNullOrWhiteSpace(kindText)
                ? "缺少必需参数: kind"
                : $"kind 只能是 {BrowserJson.Choices<BrowserWaitKind>()}，收到 '{kindText}'");
        }

        var condition = new BrowserWaitCondition(kind.Value, BrowserJson.ReadString(arguments, "value"));
        if (condition.Validate() is { } problem)
            return Failure(problem);

        var timeoutSeconds = Math.Clamp(BrowserJson.ReadInt(arguments, "timeout") ?? 30, 1, 300);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        // timeout 始终是「上限」；只有 kind=timeout 才消费 duration（固定等待时长）。
        var durationSeconds = condition.Kind == BrowserWaitKind.Timeout
            ? Math.Clamp(BrowserJson.ReadInt(arguments, "duration") ?? timeoutSeconds, 1, timeoutSeconds)
            : 0;
        var duration = condition.Kind == BrowserWaitKind.Timeout
            ? TimeSpan.FromSeconds(durationSeconds)
            : (TimeSpan?)null;

        try
        {
            var result = await page.WaitForAsync(condition, timeout, context.CancellationToken, duration)
                .ConfigureAwait(false);

            var label = condition.Kind switch
            {
                BrowserWaitKind.Selector => $"选择器 '{condition.Value}' 出现",
                BrowserWaitKind.Ref => $"元素 {condition.Value} 出现",
                BrowserWaitKind.Text => $"文本 '{condition.Value}' 出现",
                BrowserWaitKind.NetworkIdle => "网络空闲",
                _ => $"等待 {durationSeconds} 秒"
            };

            if (!result.Satisfied)
                return Failure($"等待未成立：{label}");

            var message = result.BudgetExhausted
                ? $"网络未完全空闲，已到 {timeoutSeconds} 秒预算，按就绪处理；建议随后用 `browser_snapshot` 确认页面内容"
                : $"已就绪：{label}";

            return Success(message, context.SessionId, new Dictionary<string, object>
            {
                ["kind"] = condition.Kind.ToString(),
                ["value"] = condition.Value ?? string.Empty,
                ["url"] = page.CurrentUrl,
                ["budgetExhausted"] = result.BudgetExhausted
            });
        }
        catch (OperationCanceledException)
        {
            // 用户中止或工具调用超时：不要伪装成「等待超时 N 秒」。
            return Failure("等待被取消（用户中止，或工具调用已超时）");
        }
        catch (Exception ex)
        {
            var label = condition.Kind switch
            {
                BrowserWaitKind.Selector => $"选择器 '{condition.Value}'",
                BrowserWaitKind.Ref => $"元素 {condition.Value}",
                BrowserWaitKind.Text => $"文本 '{condition.Value}'",
                BrowserWaitKind.NetworkIdle => "网络空闲",
                _ => "指定时长"
            };

            // 等待超时是最常见的结果：给出「等的是什么 + 下一步怎么办」，而不是把异常原文抛给模型
            if (ex is TimeoutException
                || ex.GetType().Name.Contains("Timeout", StringComparison.Ordinal))
            {
                return Failure($"等待超时（{timeoutSeconds} 秒）：{label} 未就绪。可改用 `browser_snapshot` 看当前页面实际内容");
            }

            return FailureFrom(ex, "等待页面");
        }
    }
}
