using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Reflection.Ui;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

/// <summary>
/// 复盘记录列表的筛选与排序（纯函数）。
/// <para>
/// 这套条件组合是记录页唯一的实质逻辑：搜索 × 触发 × 结果 × 时间 × 排序。
/// 埋在 <c>@code</c> 里就只能靠人点页面验证，而其中「今天」的边界、
/// 「仅失败」是否含失败快照、同值行的稳定次序都是容易写错又不易察觉的地方。
/// </para>
/// </summary>
public sealed class ReflectionRunFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Apply_Should_Return_All_When_No_Filter()
    {
        var runs = new[] { Run("a"), Run("b") };

        ReflectionRunFilter.Apply(runs, null, ReflectionTriggerFilter.All,
                ReflectionOutcomeFilter.All, ReflectionRangeFilter.All,
                ReflectionSortKey.Time, descending: true, now: Now)
            .Should().HaveCount(2);
    }

    [Fact]
    public void Apply_Should_Filter_By_Trigger()
    {
        var runs = new[]
        {
            Run("turn", kind: ReflectionTriggerKind.TurnNudge),
            Run("idle", kind: ReflectionTriggerKind.Idle),
            Run("manual", kind: ReflectionTriggerKind.Manual)
        };

        var result = ReflectionRunFilter.Apply(runs, null, ReflectionTriggerFilter.Idle,
            ReflectionOutcomeFilter.All, ReflectionRangeFilter.All,
            ReflectionSortKey.Time, descending: true, now: Now);

        result.Should().ContainSingle().Which.RunId.Should().Be("idle");
    }

    [Fact]
    public void Apply_Should_Filter_FailuresOnly_Via_Outcome()
    {
        // 「仅失败」从顶部 Checkbox 合并进结果下拉后，语义必须保持不变。
        var runs = new[]
        {
            Run("ok"),
            Run("bad", isFailure: true)
        };

        var result = ReflectionRunFilter.Apply(runs, null, ReflectionTriggerFilter.All,
            ReflectionOutcomeFilter.FailuresOnly, ReflectionRangeFilter.All,
            ReflectionSortKey.Time, descending: true, now: Now);

        result.Should().ContainSingle().Which.RunId.Should().Be("bad");
    }

    [Fact]
    public void Apply_Should_Filter_CreatedSkills_Separately_From_Suggestions()
    {
        var runs = new[]
        {
            Run("sugg", suggestions: 2),
            Run("created", suggestions: 1, createdSkills: 1)
        };

        var withSuggestions = ReflectionRunFilter.Apply(runs, null, ReflectionTriggerFilter.All,
            ReflectionOutcomeFilter.WithSuggestions, ReflectionRangeFilter.All,
            ReflectionSortKey.Time, descending: true, now: Now);
        var withSkills = ReflectionRunFilter.Apply(runs, null, ReflectionTriggerFilter.All,
            ReflectionOutcomeFilter.CreatedSkills, ReflectionRangeFilter.All,
            ReflectionSortKey.Time, descending: true, now: Now);

        withSuggestions.Should().HaveCount(2, "已建技能也算「有建议」出来的结果");
        withSkills.Should().ContainSingle().Which.RunId.Should().Be("created");
    }

    [Fact]
    public void Apply_Should_Match_Search_Against_RunId_And_SessionId()
    {
        var runs = new[] { Run("run_aaa", sessionId: "ses_one"), Run("run_bbb", sessionId: "ses_two") };

        var byRunId = Filter(runs, search: "aaa");
        var bySession = Filter(runs, search: "TWO");

        byRunId.Should().ContainSingle().Which.RunId.Should().Be("run_aaa");
        bySession.Should().ContainSingle().Which.RunId.Should().Be("run_bbb",
            "搜索必须大小写不敏感，否则用户输入大写就查不到");
    }

    [Fact]
    public void Apply_Should_Match_Search_Against_Session_Title()
    {
        var runs = new[] { Run("run_a", sessionId: "ses_a"), Run("run_b", sessionId: "ses_b") };

        var result = ReflectionRunFilter.Apply(runs, "修复构建", ReflectionTriggerFilter.All,
            ReflectionOutcomeFilter.All, ReflectionRangeFilter.All,
            ReflectionSortKey.Time, descending: true,
            sessionTitle: id => id == "ses_b" ? "修复构建错误" : "别的会话",
            now: Now);

        result.Should().ContainSingle().Which.RunId.Should().Be("run_b");
    }

    [Fact]
    public void Apply_Should_Treat_Blank_Search_As_No_Filter()
    {
        var runs = new[] { Run("a"), Run("b") };

        Filter(runs, search: "   ").Should().HaveCount(2);
    }

    [Fact]
    public void Apply_Should_Filter_Today_By_Calendar_Date_Not_Rolling_24h()
    {
        // 「今天」指本地日历上的今天。若写成「24 小时内」，
        // 昨天 20:00 的记录（距今 19 小时）会被错误包含，而今天 01:00 的会被错误排除。
        var runs = new[]
        {
            Run("today_early", at: new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.FromHours(8))),
            Run("yesterday_late", at: new DateTimeOffset(2026, 11, 11, 20, 0, 0, TimeSpan.FromHours(8)))
        };

        var result = Filter(runs, range: ReflectionRangeFilter.Today);

        result.Should().ContainSingle().Which.RunId.Should().Be("today_early");
    }

    [Fact]
    public void Apply_Should_Filter_Last7Days()
    {
        var runs = new[]
        {
            Run("recent", at: Now.AddDays(-3)),
            Run("old", at: Now.AddDays(-10))
        };

        Filter(runs, range: ReflectionRangeFilter.Last7Days)
            .Should().ContainSingle().Which.RunId.Should().Be("recent");
    }

    [Fact]
    public void Apply_Should_Sort_By_Time_Descending_By_Default()
    {
        var older = Run("older", at: Now.AddHours(-2));
        var newer = Run("newer", at: Now.AddHours(-1));

        var result = Filter([older, newer], sort: ReflectionSortKey.Time, descending: true);

        result.Select(r => r.RunId).Should().Equal("newer", "older");
    }

    [Fact]
    public void Apply_Should_Sort_By_Suggestions_Count()
    {
        var runs = new[] { Run("few", suggestions: 1), Run("many", suggestions: 5) };

        Filter(runs, sort: ReflectionSortKey.Suggestions, descending: true)
            .Select(r => r.RunId).Should().Equal("many", "few");
    }

    [Fact]
    public void Apply_Should_Keep_Stable_Order_For_Equal_Counts()
    {
        // 同值行必须按时间倒序稳定排列，否则每次重排都会让记录跳动。
        var runs = new[]
        {
            Run("same_old", suggestions: 2, at: Now.AddHours(-3)),
            Run("same_new", suggestions: 2, at: Now.AddHours(-1))
        };

        Filter(runs, sort: ReflectionSortKey.Suggestions, descending: true)
            .Select(r => r.RunId).Should().Equal("same_new", "same_old");
    }

    [Fact]
    public void Apply_Should_Combine_All_Filters()
    {
        var runs = new[]
        {
            Run("hit", kind: ReflectionTriggerKind.Manual, suggestions: 1, at: Now.AddHours(-1)),
            Run("wrong_kind", kind: ReflectionTriggerKind.Idle, suggestions: 1, at: Now.AddHours(-1)),
            Run("no_suggestion", kind: ReflectionTriggerKind.Manual, at: Now.AddHours(-1)),
            Run("too_old", kind: ReflectionTriggerKind.Manual, suggestions: 1, at: Now.AddDays(-20))
        };

        var result = ReflectionRunFilter.Apply(runs, null,
            ReflectionTriggerFilter.Manual, ReflectionOutcomeFilter.WithSuggestions,
            ReflectionRangeFilter.Last7Days, ReflectionSortKey.Time, descending: true, now: Now);

        result.Should().ContainSingle().Which.RunId.Should().Be("hit");
    }

    private static IReadOnlyList<ReflectionRunSummary> Filter(
        IEnumerable<ReflectionRunSummary> runs,
        string? search = null,
        ReflectionTriggerFilter trigger = ReflectionTriggerFilter.All,
        ReflectionOutcomeFilter outcome = ReflectionOutcomeFilter.All,
        ReflectionRangeFilter range = ReflectionRangeFilter.All,
        ReflectionSortKey sort = ReflectionSortKey.Time,
        bool descending = true)
        => ReflectionRunFilter.Apply(runs, search, trigger, outcome, range, sort, descending, null, Now);

    private static ReflectionRunSummary Run(
        string runId,
        string sessionId = "ses_1",
        string executionId = "exec_1",
        ReflectionTriggerKind kind = ReflectionTriggerKind.TurnNudge,
        bool isFailure = false,
        int suggestions = 0,
        int createdSkills = 0,
        DateTimeOffset? at = null)
        => new(
            SessionId: sessionId,
            ExecutionId: executionId,
            Kind: kind,
            At: at ?? Now.AddMinutes(-5),
            Outcome: isFailure ? "failed:timeout" : ReflectionRunOutcomes.Suggested,
            CreatedSkills: Enumerable.Range(0, createdSkills).Select(i => $"skill-{i}").ToList(),
            Suggestions: Enumerable.Range(0, suggestions)
                .Select(i => new ReflectionSuggestion("skill", $"target-{i}", "content", "reason", ["evidence"]))
                .ToList(),
            InputTokens: 10,
            OutputTokens: 5,
            ModelId: null,
            IsFailure: isFailure,
            Detail: null,
            FilePath: $"/runs/{runId}.json",
            RunId: runId);
}
