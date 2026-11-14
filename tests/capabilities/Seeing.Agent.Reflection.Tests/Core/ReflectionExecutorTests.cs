using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Configuration;
using Seeing.Agent.Reflection.Core;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>复盘执行器的五阶段主流程：门禁、快照、内部会话、投递等待、收集与清理。</summary>
public sealed class ReflectionExecutorTests
{
    private const string SourceSessionId = "ses_source_001";
    private const string ChildSessionId = "ses_child_001";

    /// <summary>测试固定场景：默认配置全部开启且额度充足。</summary>
    private sealed class Fixture
    {
        public Mock<ISessionManager> SessionManager { get; } = new(MockBehavior.Loose);
        public Mock<ISessionGroupManager> GroupManager { get; } = new(MockBehavior.Loose);
        public Mock<ISessionRuntime> Runtime { get; } = new(MockBehavior.Loose);
        public Mock<IReflectionJournal> Journal { get; } = new(MockBehavior.Loose);
        public Mock<IExecutionUsageLedger> Ledger { get; } = new(MockBehavior.Loose);

        public ReflectionOptions Options { get; } = new()
        {
            Enabled = true,
            Mode = ReflectionMode.SuggestOnly,
            MaxRunsPerHour = 10,
            DailyTokenBudget = 1_000_000,
            RunTimeoutMinutes = 1
        };

        public ReflectionRateLimiter RateLimiter { get; }

        public Fixture()
        {
            RateLimiter = new ReflectionRateLimiter();

            SessionManager.Setup(m => m.Get(It.IsAny<string>())).Returns((SessionData?)null);
            SessionManager.Setup(m => m.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            GroupManager.Setup(m => m.CreateInternalAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyList<SessionPermissionRule>>(), It.IsAny<string?>(),
                    It.IsAny<SessionOrigin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Child());

            GroupManager.Setup(m => m.RemoveSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Journal.Setup(j => j.AppendAsync(It.IsAny<ReflectionRunRecord>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        public SessionData Source()
        {
            var session = new SessionData
            {
                Id = SourceSessionId,
                Title = "源会话",
                Kind = SessionKind.Root,
                Scenario = "repo-fix"
            };
            session.AddMessage(SessionMessage.UserMessage("请修复构建错误"));
            return session;
        }

        public SessionData Child(string assistantText = "")
        {
            var session = new SessionData
            {
                Id = ChildSessionId,
                Title = "源会话 · 复盘",
                Kind = SessionKind.SubAgent,
                Origin = SessionOrigin.Reflection
            };

            if (!string.IsNullOrEmpty(assistantText))
            {
                session.AddMessage(SessionMessage.AssistantMessage(assistantText));
            }

            return session;
        }

        public ReflectionOptionsAccessorMonitor Monitor() => new(Options);

        public Mock<Seeing.Agent.Abstractions.Skills.ISkillWriter> SkillWriter { get; } = new(MockBehavior.Loose);

        public Mock<Seeing.Agent.Abstractions.Skills.ISkillCatalog> SkillCatalog { get; } = new(MockBehavior.Loose);

        public ReflectionExecutor Build()
            => new(
                SessionManager.Object,
                GroupManager.Object,
                Runtime.Object,
                Journal.Object,
                Ledger.Object,
                RateLimiter,
                new ReflectionSuggestionsWriter(
                    Monitor(),
                    NullLogger<ReflectionSuggestionsWriter>.Instance,
                    SkillWriter.Object,
                    SkillCatalog.Object),
                Monitor(),
                NullLogger<ReflectionExecutor>.Instance);

        public static ReflectionRequest Request()
            => new(SourceSessionId, "exec_001", ReflectionTriggerKind.TurnNudge, DateTimeOffset.UtcNow);
    }

    /// <summary>只承担「把固定配置暴露为当前值」的监控实现。</summary>
    private sealed class ReflectionOptionsAccessorMonitor : IOptionsMonitor<ReflectionOptions>
    {
        private readonly ReflectionOptions _value;

        public ReflectionOptionsAccessorMonitor(ReflectionOptions value) => _value = value;

        public ReflectionOptions CurrentValue => _value;

        public ReflectionOptions Get(string? name) => _value;

        public IDisposable? OnChange(Action<ReflectionOptions, string?> listener) => null;
    }

    /// <summary>构造一条合法的建议 JSON。</summary>
    private static string SuggestionJson()
        => """
           {"suggestions":[{"kind":"skill","target":"build-before-edit","description":"改代码前先跑构建：在编辑项目源码前执行 dotnet build。","suggestedContent":"先 dotnet build，再改代码","reason":"能提前暴露编译错误","evidence":["第 1 轮直接改了代码"]}]}
           """;

    [Fact]
    public async Task Run_Should_Skip_When_Disabled()
    {
        var fixture = new Fixture();
        fixture.Options.Enabled = false;

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.Executed.Should().BeFalse();
        result.SkipReason.Should().Be(ReflectionSkipReasons.Disabled);
        fixture.Runtime.Verify(
            r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_Should_Skip_When_Mode_Is_Off()
    {
        var fixture = new Fixture();
        fixture.Options.Mode = ReflectionMode.Off;

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.SkipReason.Should().Be(ReflectionSkipReasons.Disabled);
    }

    [Fact]
    public async Task Run_Should_Skip_When_Quota_Exhausted()
    {
        var fixture = new Fixture();
        fixture.Options.DailyTokenBudget = 100;
        fixture.Ledger.Setup(l => l.UsedToday()).Returns(100);

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.Executed.Should().BeFalse();
        result.SkipReason.Should().Be(ReflectionSkipReasons.Quota);
        fixture.Runtime.Verify(
            r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_Should_Skip_When_Rate_Limited()
    {
        var fixture = new Fixture();
        fixture.Options.MaxRunsPerHour = 1;

        // 先手工用掉唯一额度，使下一次必然被限流。
        fixture.RateLimiter.TryAcquire(1, DateTimeOffset.UtcNow).Should().BeTrue();

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.SkipReason.Should().Be(ReflectionSkipReasons.RateLimit);
    }

    [Fact]
    public async Task Run_Should_Skip_When_Source_Missing()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionData?)null);

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.SkipReason.Should().Be(ReflectionSkipReasons.NoSource);
    }

    [Fact]
    public async Task Run_Should_Build_Internal_Session_With_Reflection_Origin()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));

        await fixture.Build().RunAsync(Fixture.Request());

        fixture.GroupManager.Verify(
            g => g.CreateInternalAsync(
                SourceSessionId,
                "reflection",
                It.Is<string>(t => t.Contains("· 复盘", StringComparison.Ordinal)),
                It.IsAny<IReadOnlyList<SessionPermissionRule>>(),
                "repo-fix",
                SessionOrigin.Reflection,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_Should_Return_Timeout_When_Business_Timeout_Hits()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // 外层 ct 未取消，因此这是业务超时而不是宿主取消。
        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.Executed.Should().BeFalse();
        result.SkipReason.Should().Be(ReflectionSkipReasons.Timeout);
    }

    [Fact]
    public async Task Run_Should_Return_Failed_When_Outcome_Not_Completed()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Failed));

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.SkipReason.Should().Be(ReflectionSkipReasons.Failed);
    }

    [Fact]
    public async Task Run_Should_Record_Suggestions_To_Journal()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.Executed.Should().BeTrue();
        result.SuggestionCount.Should().Be(1);
        fixture.Journal.Verify(
            j => j.AppendAsync(
                It.Is<ReflectionRunRecord>(r => r.Suggestions.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// journal 的 <c>Outcome</c> 必须是「复盘结果」而不是执行状态。
    /// <para>
    /// 曾经这里写的是 <c>outcome.Status.ToString()</c> → 落盘 <c>"Completed"</c>，
    /// 而 UI 只映射 suggested/created/skipped/failed：记录页显示英文、统计恒为 0，
    /// 用户看到的就是「功能没工作」。这条断言把语义钉在数据层。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Run_Should_Record_Semantic_Outcome_Not_Execution_Status()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));

        await fixture.Build().RunAsync(Fixture.Request());

        fixture.Journal.Verify(
            j => j.AppendAsync(
                It.Is<ReflectionRunRecord>(r => r.Outcome == ReflectionRunOutcomes.Suggested),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_Should_Record_Skipped_Outcome_When_Model_Says_Skip()
    {
        // 「模型合法地表示本轮没建议」是一个成功的结果，不是失败，也不是 Created。
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child("""{"action":"skip"}"""));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));

        await fixture.Build().RunAsync(Fixture.Request());

        fixture.Journal.Verify(
            j => j.AppendAsync(
                It.Is<ReflectionRunRecord>(r => r.Outcome == ReflectionRunOutcomes.Skipped),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// ApplyCreate 模式下真的写入了技能时，Outcome 必须是 <c>created</c>：
    /// 否则「建议有、技能也写了」和「只有建议」在记录页上无法区分。
    /// </summary>
    [Fact]
    public async Task Run_Should_Record_Created_Outcome_When_Skills_Written()
    {
        var fixture = new Fixture();
        fixture.Options.Mode = ReflectionMode.ApplyCreate;
        fixture.Options.Model = null;
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));
        fixture.SkillCatalog.Setup(c => c.Find(It.IsAny<string>())).Returns((Seeing.Agent.Abstractions.Skills.SkillCatalogEntry?)null);
        fixture.SkillWriter
            .Setup(w => w.CreateAsync(
                It.IsAny<Seeing.Agent.Abstractions.Skills.SkillDraft>(),
                It.IsAny<Seeing.Agent.Abstractions.Configuration.ConfigLevel>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Seeing.Agent.Abstractions.Skills.SkillWriteResult(true, null, null));

        await fixture.Build().RunAsync(Fixture.Request());

        fixture.Journal.Verify(
            j => j.AppendAsync(
                It.Is<ReflectionRunRecord>(r => r.Outcome == ReflectionRunOutcomes.Created
                                                && r.CreatedSkills.Count == 1),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_Should_Treat_Empty_Parse_As_Executed_Without_Suggestions()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child("""{"action":"skip"}"""));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.Executed.Should().BeTrue();
        result.SkipReason.Should().BeNull();
        result.SuggestionCount.Should().Be(0);
    }

    [Fact]
    public async Task Run_Should_Cleanup_Session_On_Success()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));

        await fixture.Build().RunAsync(Fixture.Request());

        fixture.GroupManager.Verify(
            g => g.RemoveSessionAsync(ChildSessionId, It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.SessionManager.Verify(
            m => m.DeleteAsync(ChildSessionId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_Should_Record_Failure_And_Cleanup_Session_On_Timeout()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.SkipReason.Should().Be(ReflectionSkipReasons.Timeout);

        // 失败也要清理：原先「保留供诊断」没有回收机制，持续失败会不断堆积孤儿会话。
        // 诊断信息改落 .fail.json。
        fixture.GroupManager.Verify(
            g => g.RemoveSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.SessionManager.Verify(
            m => m.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.Journal.Verify(
            j => j.AppendFailureAsync(
                It.Is<Seeing.Agent.Reflection.Abstractions.ReflectionFailureRecord>(
                    r => r.SkipReason == ReflectionSkipReasons.Timeout),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_Should_Not_Throw_When_Journal_Throws()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));
        fixture.Journal
            .Setup(j => j.AppendAsync(It.IsAny<ReflectionRunRecord>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("磁盘已满"));

        var act = async () => await fixture.Build().RunAsync(Fixture.Request());

        // 日志是旁路产物：写失败降级为 failed，异常不外泄给 Worker。
        var result = await act.Should().NotThrowAsync();
        result.Subject.Executed.Should().BeFalse();
        result.Subject.SkipReason.Should().Be(ReflectionSkipReasons.Failed);
    }

    /// <summary>
    /// 用量必须真的记进账本：它是「每日预算」闸的唯一输入端，
    /// 记账漏了预算就永远不满，能力会一直跑下去。
    /// </summary>
    [Fact]
    public async Task Run_Should_Record_Usage_To_Ledger()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));

        await fixture.Build().RunAsync(Fixture.Request());

        fixture.Ledger.Verify(
            l => l.Add(It.Is<Seeing.Agent.Abstractions.Llm.TokenUsage>(
                u => u.InputTokens == 100 && u.OutputTokens == 50)),
            Times.Once);
    }

    /// <summary>
    /// 记账失败不得连累清理：否则磁盘权限问题会让每次复盘都留下孤儿子会话。
    /// </summary>
    [Fact]
    public async Task Run_Should_Still_Cleanup_When_Ledger_Throws()
    {
        var fixture = new Fixture();
        fixture.SessionManager
            .Setup(m => m.TryGetOrLoadAsync(SourceSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixture.Source());
        fixture.SessionManager.Setup(m => m.Get(ChildSessionId)).Returns(fixture.Child(SuggestionJson()));
        fixture.Runtime
            .Setup(r => r.DeliverAndAwaitAsync(It.IsAny<InboxMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Outcome(ExecutionStatus.Completed));
        fixture.Ledger.Setup(l => l.Add(It.IsAny<Seeing.Agent.Abstractions.Llm.TokenUsage>()))
            .Throws(new UnauthorizedAccessException("账本文件不可写"));

        var result = await fixture.Build().RunAsync(Fixture.Request());

        result.Executed.Should().BeTrue("记账是旁路动作，失败不应让已完成的复盘变成失败");
        fixture.GroupManager.Verify(
            g => g.RemoveSessionAsync(ChildSessionId, It.IsAny<CancellationToken>()),
            Times.Once,
            "记账抛异常时子会话仍必须被清理");
    }

    /// <summary>构造一次终态快照。</summary>
    private static ExecutionOutcome Outcome(ExecutionStatus status)
        => new()
        {
            SessionId = ChildSessionId,
            ExecutionId = "exec_child_001",
            Status = status,
            AgentExecuted = true,
            Usage = new Seeing.Agent.Abstractions.Llm.TokenUsage { InputTokens = 100, OutputTokens = 50 }
        };
}
