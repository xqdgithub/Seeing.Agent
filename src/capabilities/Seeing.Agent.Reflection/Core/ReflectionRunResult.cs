namespace Seeing.Agent.Reflection.Core;

/// <summary>
/// 一次复盘执行的结果。
/// <para>
/// 刻意不抛异常：复盘是后台尽力而为的旁路能力，所有「没做」「做不了」都必须
/// 以数据形式返回，由 Worker 决定是否记录，避免异常在后台循环里被吞掉后无从排查。
/// </para>
/// </summary>
/// <param name="Executed">是否真正走完了「投递 + 收集」两个阶段（成功产出或无建议的空产出都算）。</param>
/// <param name="SkipReason">未执行的原因，取自 <see cref="ReflectionSkipReasons"/>；已执行时为 <c>null</c>。</param>
/// <param name="CreatedSkills">本次实际创建的技能名（由 <see cref="ReflectionSuggestionsWriter"/> 在执行器内写入，仅 <c>ApplyCreate</c> 模式非空）。</param>
/// <param name="SuggestionCount">本次解析到的建议条数；已执行但无建议时为 0。</param>
public sealed record ReflectionRunResult(
    bool Executed,
    string? SkipReason,
    IReadOnlyList<string> CreatedSkills,
    int SuggestionCount)
{
    /// <summary>构造一个「未执行」结果。</summary>
    /// <param name="reason">原因常量，取自 <see cref="ReflectionSkipReasons"/>。</param>
    public static ReflectionRunResult Skipped(string reason)
        => new(false, reason, Array.Empty<string>(), 0);
}

/// <summary>
/// 复盘运行结果（journal 的 <c>Outcome</c> 字段）的取值常量。
/// <para>
/// 收敛为常量而非自由字符串：UI 按这些值做文案映射，散落的字面量迟早漂移——
/// 曾经这里写的是执行状态（<c>Completed</c>），页面因此永远显示英文并让统计恒为 0。
/// </para>
/// </summary>
public static class ReflectionRunOutcomes
{
    /// <summary>产出了建议（未写盘）。</summary>
    public const string Suggested = "suggested";

    /// <summary>产出了建议并实际创建了技能（仅 <c>ApplyCreate</c> 模式）。</summary>
    public const string Created = "created";

    /// <summary>执行完成但没有产出（模型合法地表达了「本轮没有建议」）。</summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// 复盘跳过原因常量。
/// <para>
/// 收敛为常量而非自由字符串：日志与测试都要按原因判定，散落的字面量迟早会漂移。
/// </para>
/// </summary>
public static class ReflectionSkipReasons
{
    /// <summary>总开关关闭，或模式为 <see cref="Configuration.ReflectionMode.Off"/>。</summary>
    public const string Disabled = "disabled";

    /// <summary>今日 Token 用量已达 <see cref="Configuration.ReflectionOptions.DailyTokenBudget"/>。</summary>
    public const string Quota = "quota";

    /// <summary>最近一小时的执行次数已达 <see cref="Configuration.ReflectionOptions.MaxRunsPerHour"/>。</summary>
    public const string RateLimit = "rate-limit";

    /// <summary>源会话不存在（内存与存储都未命中），没有可复盘的正文。</summary>
    public const string NoSource = "no-source";

    /// <summary>业务超时：自带超时先于外层取消触发，子会话保留供诊断。</summary>
    public const string Timeout = "timeout";

    /// <summary>执行未进入 Completed 终态，或运行期抛出未预期异常。</summary>
    public const string Failed = "failed";

    /// <summary>执行成功但读不到子会话的最后一条 assistant 文本。</summary>
    public const string ParseFailed = "parse-failed";
}
