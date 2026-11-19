using System.Text.Json;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 元素定位：ref 优先，CSS 选择器后备，坐标最后。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么 ref 优先</b>：模型写 CSS 选择器的错误率高（凭据、层级、动态类名），而
/// <c>data-seeing-ref</c> 是快照<b>现场打上去的</b>，写错概率接近零。
/// </para>
/// <para>
/// <b>为什么保留 selector</b>：ref 在导航后失效；用户明确知道结构时（或脚本化调用）用选择器更直接。
/// </para>
/// <para>
/// <b>为什么保留坐标</b>：canvas 上的人工操作走坐标；agent 偶尔也需要点视觉位置（如画布内元素）。
/// </para>
/// </remarks>
public readonly record struct BrowserElementTarget
{
    /// <summary>元素引用（<c>e1</c> 形式）。</summary>
    public string? Ref { get; init; }

    /// <summary>CSS 选择器。</summary>
    public string? Selector { get; init; }

    /// <summary>视口 X 坐标（CSS 像素）。</summary>
    public double? X { get; init; }

    /// <summary>视口 Y 坐标（CSS 像素）。</summary>
    public double? Y { get; init; }

    /// <summary>是否给定坐标。</summary>
    public bool HasCoordinates => X is not null && Y is not null;

    /// <summary>是否给定了元素定位（ref 或 selector）。</summary>
    public bool HasElement => !string.IsNullOrWhiteSpace(Ref) || !string.IsNullOrWhiteSpace(Selector);

    /// <summary>是否完全没有定位信息。</summary>
    public bool IsEmpty => !HasElement && !HasCoordinates;

    /// <summary>取 CSS 选择器（ref 转成属性选择器；两者都空返回 null）。</summary>
    public string? ResolveSelector()
    {
        if (!string.IsNullOrWhiteSpace(Ref))
        {
            // 唯一构造点做形状校验：任何把 ref 拼进选择器的路径（click/type/scroll/screenshot/
            // wait_for 的 kind=ref）都必须经过这里，否则「ref 只能是快照编号」的边界
            // 会随调用方不同而不同（wait_for 曾完全绕过）。
            if (!HasValidRefShape())
            {
                throw new BrowserElementValidationException(
                    $"ref '{Ref.Trim()}' 形状不合法：应为 browser_snapshot 返回的编号（如 e1、e12）");
            }

            return $"[data-seeing-ref=\"{Ref.Trim()}\"]";
        }

        return string.IsNullOrWhiteSpace(Selector) ? null : Selector.Trim();
    }

    /// <summary>
    /// ref 是否形如 <c>e1</c>/<c>e42</c>（快照打标的唯一合法形状）。
    /// </summary>
    /// <remarks>
    /// ref 会被拼进 CSS 属性选择器。不校验形状时模型可通过 ref 注入选择器片段
    /// （如 <c>e1"] , body</c>），命中快照之外的任意元素——「ref 只能命中快照打标元素」
    /// 这条安全叙事就不成立了。虽然同一模型本来就有 <c>browser_eval</c>，不构成额外权限，
    /// 但把 ref 的语义收窄到「只能是快照给的编号」成本极低，且让失败文案更准确。
    /// </remarks>
    public bool HasValidRefShape()
        => string.IsNullOrWhiteSpace(Ref)
           || (Ref.Trim().Length > 1
               && Ref.Trim()[0] == 'e'
               && Ref.Trim().Skip(1).All(char.IsAsciiDigit));

    /// <summary>校验并给出可读原因（成功返回 null）。</summary>
    public string? Validate()
    {
        // 半对坐标优先报告：这是手误，比「什么也没给」更需要精确提示
        if (X is null != Y is null)
            return "x 与 y 必须同时提供";

        if (IsEmpty)
            return "缺少元素定位：请提供 ref（由 browser_snapshot 获得）、selector 或 x+y 坐标";

        if (HasCoordinates && HasElement)
            return "ref/selector 与 x/y 坐标只能二选一";

        if (!HasValidRefShape())
            return $"ref '{Ref!.Trim()}' 形状不合法：应为 browser_snapshot 返回的编号（如 e1、e12）";

        return null;
    }

    /// <summary>渲染成诊断文本。</summary>
    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(Ref))
            return Ref.Trim();

        if (!string.IsNullOrWhiteSpace(Selector))
            return Selector.Trim();

        return HasCoordinates
            ? $"({X!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
              $"{Y!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : "(未指定)";
    }
}

/// <summary>元素定位参数不合法（ref 形状错误等）。</summary>
public sealed class BrowserElementValidationException : Exception
{
    /// <summary>创建异常。</summary>
    public BrowserElementValidationException(string message) : base(message)
    {
    }
}

/// <summary>等待条件种类。</summary>
public enum BrowserWaitKind
{
    /// <summary>等待 CSS 选择器出现。</summary>
    Selector,

    /// <summary>等待 ref 对应的元素出现。</summary>
    Ref,

    /// <summary>等待页面可见文本包含指定内容。</summary>
    Text,

    /// <summary>等待网络空闲。</summary>
    NetworkIdle,

    /// <summary>固定等待指定时长。</summary>
    Timeout
}

/// <summary>等待条件。</summary>
/// <param name="Kind">条件种类。</param>
/// <param name="Value">条件值（<see cref="BrowserWaitKind.Selector"/>/<see cref="BrowserWaitKind.Text"/> 使用）。</param>
public readonly record struct BrowserWaitCondition(BrowserWaitKind Kind, string? Value)
{
    /// <summary>校验并给出可读原因（成功返回 null）。</summary>
    public string? Validate()
        => Kind is BrowserWaitKind.Selector or BrowserWaitKind.Text
           && string.IsNullOrWhiteSpace(Value)
            ? $"kind={Kind.ToString().ToLowerInvariant()} 时 value 是必需的"
            : null;
}

/// <summary>等待结果。</summary>
/// <param name="Satisfied">是否可按就绪处理。</param>
/// <param name="BudgetExhausted">是否因预算用尽而按就绪处理（目前只用于 networkidle）。</param>
public readonly record struct BrowserWaitResult(bool Satisfied, bool BudgetExhausted)
{
    /// <summary>条件已成立。</summary>
    public static BrowserWaitResult Ready => new(true, false);

    /// <summary>预算用尽，但按约定视为可用（networkidle）。</summary>
    public static BrowserWaitResult ReadyAfterBudget => new(true, true);
}

/// <summary>滚动方向。</summary>
public enum BrowserScrollDirection
{
    /// <summary>向上。</summary>
    Up,

    /// <summary>向下。</summary>
    Down,

    /// <summary>向左。</summary>
    Left,

    /// <summary>向右。</summary>
    Right
}

/// <summary>导航等待策略。</summary>
public enum BrowserWaitUntil
{
    /// <summary>等待 <c>load</c> 事件（含全部子资源）。</summary>
    Load,

    /// <summary>等待 <c>DOMContentLoaded</c>（默认：绝大多数场景够用且明显更快）。</summary>
    DomContentLoaded,

    /// <summary>等待网络空闲（无未完成请求 ≥500ms）。</summary>
    NetworkIdle
}

/// <summary>导航结果。</summary>
/// <param name="Url">导航后的地址。</param>
/// <param name="Title">导航后的标题。</param>
/// <param name="Status">HTTP 状态码（非 http 页面为 null）。</param>
/// <param name="ElapsedMs">耗时（毫秒）。</param>
public sealed record BrowserNavigationResult(string Url, string Title, int? Status, long ElapsedMs)
{
    /// <summary>渲染成给模型看的一行。</summary>
    public string Render()
    {
        var title = string.IsNullOrWhiteSpace(Title) ? "(无标题)" : Title;
        var status = Status is { } code ? $" · HTTP {code}" : string.Empty;
        return $"{title}（{Url}{status}）";
    }
}

/// <summary>输入类参数的单一真源（工具 schema 与执行层共用）。</summary>
/// <remarks>
/// <b>为什么集中在一处</b>：默认值与上下限曾同时在工具层和引擎层各写一份
/// （<c>600</c>、<c>1..3</c>、<c>0..500</c>），改一处漏一处就会漂移 ——
/// 工具说「最多 3 次」，执行层按别的数夹，调用方无从判断哪个是真的。
/// </remarks>
public static class BrowserInputLimits
{
    /// <summary>默认滚动距离（像素）。</summary>
    public const int DefaultScrollPx = 600;

    /// <summary>滚动距离上限（像素）。</summary>
    public const int MaxScrollPx = 20_000;

    /// <summary>点击次数下限。</summary>
    public const int MinClickCount = 1;

    /// <summary>点击次数上限（3 = 三击）。</summary>
    public const int MaxClickCount = 3;

    /// <summary>逐字输入延迟上限（毫秒）。</summary>
    public const int MaxTypeDelayMs = 500;
}

/// <summary>JSON 解析辅助（工具参数读取的唯一入口，避免各处重复写 JsonElement 样板）。</summary>
internal static class BrowserJson
{
    public static string? ReadString(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? ReadInt(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>
    /// 读取整数参数并校验范围，区分「未提供」与「提供了但不合法」。
    /// </summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="name">参数名。</param>
    /// <param name="min">最小值（含）。</param>
    /// <param name="max">最大值（含）。</param>
    /// <param name="value">读到的值；未提供时为 <see langword="null"/>。</param>
    /// <returns>格式或范围错误原因；<see langword="null"/> 表示读取成功（含「未提供」）。</returns>
    /// <remarks>
    /// <b>为什么不复用 <see cref="ReadInt"/></b>：它对类型不对的值返回 <see langword="null"/>，
    /// 与「未提供」无法区分，于是 <c>"maxElements": "300"</c> 会静默回落到默认值 ——
    /// 调用方以为参数生效了，实际用的是另一套值。参数是契约，错了要说出来。
    /// </remarks>
    public static string? TryReadInt(
        JsonElement arguments, string name, int min, int max, out int? value)
    {
        value = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
            return $"{name} 必须是整数；收到 {element.GetRawText()}";

        if (number < min || number > max)
            return $"{name} 范围 {min}–{max}；收到 {number}";

        value = number;
        return null;
    }

    /// <summary>
    /// 读取布尔参数，区分「未提供」与「提供了但不是布尔值」。
    /// </summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="name">参数名。</param>
    /// <param name="value">读到的值；未提供时为 <see langword="null"/>。</param>
    /// <returns>格式错误原因；<see langword="null"/> 表示读取成功（含「未提供」）。</returns>
    /// <remarks>
    /// <b>为什么这条更关键</b>：布尔参数的默认值往往是「有副作用的那一支」（<c>clear=true</c>
    /// 会清空输入框）。<c>"false"</c> 这种字符串一旦被静默当成「未提供」，
    /// 工具就会执行与调用方意图相反的破坏性动作。
    /// </remarks>
    public static string? TryReadBool(JsonElement arguments, string name, out bool? value)
    {
        value = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return $"{name} 必须是布尔值 true/false；收到 {element.GetRawText()}";

        value = element.GetBoolean();
        return null;
    }

    /// <summary>
    /// 读取整数毫秒参数，并区分「未提供」与「提供了但不是整数」。
    /// </summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="name">参数名（带 Ms 后缀，单位即毫秒）。</param>
    /// <param name="value">读到的毫秒数；未提供时为 <see langword="null"/>。</param>
    /// <returns>格式错误原因；<see langword="null"/> 表示读取成功（含「未提供」）。</returns>
    /// <remarks>
    /// <b>为什么不复用 <see cref="ReadInt"/></b>：它对类型不对的值返回 <see langword="null"/>，
    /// 与「未提供」无法区分，于是 <c>"timeoutMs": "5000"</c> 会静默回落到默认预算 ——
    /// 这正是「参数填错却看不出」的一类问题。时长参数必须把这种输入显式顶回去。
    /// </remarks>
    public static string? TryReadMilliseconds(JsonElement arguments, string name, out int? value)
    {
        value = null;

        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var ms))
            return $"{name} 必须是整数毫秒；收到 {element.GetRawText()}";

        value = ms;
        return null;
    }

    public static double? ReadDouble(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? number
            : null;

    public static bool? ReadBool(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>读取枚举型字符串参数（大小写不敏感）；未知值返回 null。</summary>
    public static TEnum? ReadEnum<TEnum>(JsonElement arguments, string name) where TEnum : struct, Enum
    {
        var text = ReadString(arguments, name);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return Enum.TryParse<TEnum>(text.Trim(), ignoreCase: true, out var parsed) ? parsed : null;
    }

    /// <summary>渲染枚举的合法取值（用于失败文案）。</summary>
    public static string Choices<TEnum>() where TEnum : struct, Enum
        => string.Join(", ", Enum.GetValues<TEnum>().Select(v => v.ToString().ToLowerInvariant()));

    /// <summary>
    /// 旧字段出现时给出改名指引。
    /// </summary>
    /// <param name="arguments">工具参数。</param>
    /// <param name="legacyName">已废弃的字段名。</param>
    /// <param name="newName">替代字段名。</param>
    /// <param name="unit">新字段的单位（如「毫秒」「像素」）。</param>
    /// <param name="example">替代写法的示例（如 <c>amountPx=600</c>）。</param>
    /// <returns>失败原因；旧字段未出现时返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>为什么不做静默兼容</b>：旧字段往往没有单位后缀，把它按两种单位理解都能「跑通」，
    /// 正是这种模糊让 <c>timeout=15000</c> 变成了 300 秒。明确失败一次，下一轮就会用对字段。
    /// </remarks>
    public static string? RejectObsoleteField(
        JsonElement arguments, string legacyName, string newName, string unit, string example)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(legacyName, out _)
            ? $"{legacyName} 已更名为 {newName}（{unit}）；例如 {example}"
            : null;
}
