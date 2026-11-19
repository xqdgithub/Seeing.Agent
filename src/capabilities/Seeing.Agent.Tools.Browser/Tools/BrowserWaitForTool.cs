using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;
using System.Text.Json;

namespace Seeing.Agent.Tools.Browser.Tools;

/// <summary>
/// <c>browser_wait_for</c>：等待元素出现、文本出现、网络空闲或固定时长。
/// </summary>
/// <remarks>
/// <para>
/// 页面加载完不代表内容就绪（前端渲染、接口回填）。不等待就去快照，会拿到空列表并让 agent
/// 误判「页面上没有这个按钮」。这个工具是那种误判的解药。
/// </para>
/// <para>
/// <b>时长参数一律用毫秒</b>（<c>timeoutMs</c> / <c>durationMs</c>）：参数名自带单位，
/// 调用方没有「按秒还是按毫秒」的二义。旧实现用不带单位的 <c>timeout</c> + <c>Math.Clamp</c>，
/// 把 15000（本意 15 秒）静默夹成 300 秒 —— 这才是「browser_wait_for 一直在等待」的真正成因。
/// 现在越界一律失败并说明单位，绝不夹取。
/// </para>
/// </remarks>
[ToolCapability(ToolCapabilityKeys.TimeoutBudget, "130000")]
public sealed class BrowserWaitForTool : BrowserToolBase
{
    /// <summary>默认等待预算（毫秒）。</summary>
    public const int DefaultTimeoutMs = 30_000;

    /// <summary>等待时长下限（毫秒）。低于它的等待没有实际意义，通常是单位填错。</summary>
    public const int MinWaitMs = 50;

    /// <summary>等待时长上限（毫秒）。</summary>
    public const int MaxWaitMs = 120_000;

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
        "- 所有时长参数都以毫秒计：timeoutMs（预算）与 durationMs（固定等待）。\n" +
        "- kind=selector 需给 value（CSS 选择器）；kind=text 需给 value（页面可见文本片段）。\n" +
        "- kind=ref 等待某个快照 ref 对应的元素出现。\n" +
        "- kind=networkidle 等网络空闲（连续 500 毫秒无未完成请求）。真实站点可能一直有零星请求，此时到预算会按就绪返回并注明。\n" +
        "- kind=timeout 固定等待 durationMs（缺省等于 timeoutMs）；timeoutMs 始终是整次等待的上限。\n" +
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
            timeoutMs = new
            {
                type = "integer",
                minimum = MinWaitMs,
                maximum = MaxWaitMs,
                @default = DefaultTimeoutMs,
                description = $"最长等待毫秒数（默认 {DefaultTimeoutMs}，范围 {MinWaitMs}–{MaxWaitMs}）。对所有 kind 都是上限；越界会失败，不会被截断"
            },
            durationMs = new
            {
                type = "integer",
                minimum = MinWaitMs,
                maximum = MaxWaitMs,
                description = "仅 kind=timeout：固定等待毫秒数（缺省等于 timeoutMs，且不得超过它）"
            }
        },
        required = new[] { "kind" }
    });

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
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

        // 参数校验排在取页面之前：单位填错与「还没打开页面」无关，不该被后者挡住。
        if (BrowserJson.RejectObsoleteField(arguments, "timeout", "timeoutMs", "毫秒", "timeoutMs=15000") is { } legacyTimeout)
            return Failure(legacyTimeout);

        if (BrowserJson.RejectObsoleteField(arguments, "duration", "durationMs", "毫秒", "durationMs=3000") is { } legacyDuration)
            return Failure(legacyDuration);

        if (BrowserJson.TryReadMilliseconds(arguments, "timeoutMs", out var timeoutValue) is { } timeoutProblem)
            return Failure(timeoutProblem);

        var timeoutMs = timeoutValue ?? DefaultTimeoutMs;
        if (timeoutMs is < MinWaitMs or > MaxWaitMs)
        {
            return Failure(
                $"timeoutMs 单位是毫秒，范围 {MinWaitMs}–{MaxWaitMs}；收到 {timeoutMs}" +
                "（例如等 15 秒请传 15000）");
        }

        if (BrowserJson.TryReadMilliseconds(arguments, "durationMs", out var durationValue) is { } durationProblem)
            return Failure(durationProblem);

        var durationMs = 0;
        if (condition.Kind == BrowserWaitKind.Timeout)
        {
            durationMs = durationValue ?? timeoutMs;
            if (durationMs is < MinWaitMs or > MaxWaitMs)
            {
                return Failure(
                    $"durationMs 单位是毫秒，范围 {MinWaitMs}–{MaxWaitMs}；收到 {durationMs}" +
                    "（例如等 3 秒请传 3000）");
            }

            if (durationMs > timeoutMs)
                return Failure($"durationMs（{durationMs}）不得超过 timeoutMs（{timeoutMs}）");
        }
        else if (durationValue is not null)
        {
            return Failure($"durationMs 仅用于 kind=timeout；kind={condition.Kind.ToString().ToLowerInvariant()} 请用 timeoutMs 表达预算");
        }

        if (!TryGetPage(context.SessionId, out var page, out var failure))
            return failure!;

        var timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var duration = condition.Kind == BrowserWaitKind.Timeout
            ? TimeSpan.FromMilliseconds(durationMs)
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
                _ => $"等待 {durationMs} 毫秒"
            };

            if (!result.Satisfied)
                return Failure($"等待未成立：{label}");

            var message = result.BudgetExhausted
                ? $"网络未完全空闲，已到 {timeoutMs} 毫秒预算，按就绪处理；建议随后用 `browser_snapshot` 确认页面内容"
                : $"已就绪：{label}";

            return Success(message, context.SessionId, new Dictionary<string, object>
            {
                ["kind"] = condition.Kind.ToString(),
                ["value"] = condition.Value ?? string.Empty,
                ["url"] = page.CurrentUrl,
                ["timeoutMs"] = timeoutMs,
                ["budgetExhausted"] = result.BudgetExhausted
            });
        }
        catch (OperationCanceledException)
        {
            // 用户中止或工具调用超时：不要伪装成「等待超时 N 毫秒」。
            return Failure("等待被取消（用户中止，或工具调用已超时）");
        }
        catch (TimeoutException)
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
            return Failure($"等待超时（{timeoutMs} 毫秒）：{label} 未就绪。可改用 `browser_snapshot` 看当前页面实际内容");
        }
        catch (Exception ex)
        {
            return FailureFrom(ex, "等待页面");
        }
    }
}
