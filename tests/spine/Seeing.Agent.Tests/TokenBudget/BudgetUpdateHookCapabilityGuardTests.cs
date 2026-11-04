using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.TokenBudget;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Agent.Tests.TokenBudget;

/// <summary>
/// H-3：<see cref="BudgetUpdateHook"/> 的 chat 守卫（spec §7.6.1 / 决议 1）。
/// <para>非 chat 能力的 usage 只进 <c>llm.after_complete</c> 载荷，不写 <c>SessionData.Cached*</c>
/// 预算账；守卫必须在取会话之前，否则无会话的 embedding 后台 worker 会反复空转进 Hook。</para>
/// </summary>
public class BudgetUpdateHookCapabilityGuardTests
{
    private const string SessionId = "s1";

    /// <summary>能力键值出自 <see cref="LlmCapabilityMap"/>：连测试也不手拼 capability 字符串（决议 18）。</summary>
    private static readonly string ChatCapability = LlmCapabilityMap.ToHookValue(LlmCapabilityKind.Chat);

    [Fact]
    public void Spec_ShouldSubscribeLlmAfterComplete()
    {
        var hook = CreateHook(out _, out _);

        hook.Spec.Point.Should().Be("llm.after_complete");
    }

    [Theory]
    // InlineData 只能是常量：这几枚是载荷的 wire 形状样本，产出端仍统一走 LlmCapabilityMap
    [InlineData("embedding")]
    [InlineData("decision")]
    [InlineData("speech.synthesis")]
    [InlineData("speech.transcription")]
    public async Task ExecuteAsync_NonChatCapability_ShouldNotRecordSessionBudget(string capability)
    {
        // 会话可用但能力非 chat：预算账一格都不该动（决议 1）
        var session = new SessionData { Id = SessionId };
        var hook = CreateHook(out var sessionManager, out var budgetManager, session);
        var payload = Payload(capability, new TokenUsage { InputTokens = 5000, OutputTokens = 10 });

        var result = await hook.ExecuteAsync(payload);

        result.Should().Be(HookResult.Success);
        session.CachedInputTokens.Should().BeNull("非 chat 能力不得写会话预算账");
        session.CachedOutputTokens.Should().BeNull();
        // 守卫在取会话之前：非 chat 调用连「加载会话」都不该发生（无会话的后台 worker 不空转）
        sessionManager.Verify(
            m => m.GetOrLoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budgetManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ChatPayloadWithUsage_ShouldCacheUsageAndSaveSession()
    {
        var session = new SessionData { Id = SessionId };
        var hook = CreateHook(out var sessionManager, out var budgetManager, session);
        var payload = Payload(ChatCapability, new TokenUsage { InputTokens = 100, OutputTokens = 20 });

        var result = await hook.ExecuteAsync(payload);

        result.Should().Be(HookResult.Success);
        session.CachedInputTokens.Should().Be(100);
        session.CachedOutputTokens.Should().Be(20);
        budgetManager.Verify(b => b.GetBudgetStatus(session, It.IsAny<string?>()), Times.Once);
        sessionManager.Verify(
            m => m.SaveAndNotifyAsync(SessionId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ChatWithoutSessionId_ShouldStillShortCircuit()
    {
        var hook = CreateHook(out var sessionManager, out var budgetManager);
        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            sessionId: "",
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = ChatCapability,
                [HookDataContract.LlmAfterComplete.Usage.Name] = new TokenUsage { InputTokens = 9, OutputTokens = 1 },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var result = await hook.ExecuteAsync(payload);

        result.Should().Be(HookResult.Success);
        // 保持现状语义：无会话归属 → 不记账、不加载、不通知预算
        sessionManager.Verify(
            m => m.GetOrLoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budgetManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ChatSessionMissing_ShouldNotThrowAndNotRecord()
    {
        // 真实契约：ISessionManager.GetOrLoadAsync 在会话不存在时抛 InvalidOperationException
        var hook = CreateHook(out var sessionManager, out var budgetManager);
        sessionManager
            .Setup(m => m.GetOrLoadAsync(SessionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("会话不存在"));
        var payload = Payload(ChatCapability, new TokenUsage { InputTokens = 100, OutputTokens = 20 });

        var act = async () => await hook.ExecuteAsync(payload);

        await act.Should().NotThrowAsync("预算更新失败不得阻断执行链");
        sessionManager.Verify(
            m => m.SaveAndNotifyAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        budgetManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_MissingCapabilityKey_ShouldNotRecord()
    {
        // 严格口径（§7.6.1「只在 capability == chat 时记账」）：载荷没声明能力就不算 chat
        var session = new SessionData { Id = SessionId };
        var hook = CreateHook(out var sessionManager, out var budgetManager, session);
        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            SessionId,
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Usage.Name] = new TokenUsage { InputTokens = 100 },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await hook.ExecuteAsync(payload);

        session.CachedInputTokens.Should().BeNull();
        sessionManager.Verify(
            m => m.GetOrLoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        budgetManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ChatInInputOnly_ShouldStillRecord()
    {
        // 键位置兜底：适配器写在 result 里，历史/旁路调用方可能只在 input 带 capability
        var session = new SessionData { Id = SessionId };
        var hook = CreateHook(out _, out _, session);
        var payload = HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            SessionId,
            input: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = ChatCapability,
            },
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Usage.Name] = new TokenUsage { InputTokens = 7, OutputTokens = 3 },
            },
            cancellationToken: TestContext.Current.CancellationToken);

        await hook.ExecuteAsync(payload);

        session.CachedInputTokens.Should().Be(7);
    }

    private static HookPayload Payload(string capability, TokenUsage usage) =>
        HookPayload.FireAndForget(
            HookRegistry.LlmAfterComplete,
            SessionId,
            result: new Dictionary<string, object?>
            {
                [HookDataContract.LlmAfterComplete.Capability.Name] = capability,
                [HookDataContract.LlmAfterComplete.Usage.Name] = usage,
            });

    /// <summary>装配一个用严格 Mock 的 Hook；<paramref name="session"/> 非空时会话读取即命中它。</summary>
    private static BudgetUpdateHook CreateHook(
        out Mock<ISessionManager> sessionManager,
        out Mock<ITokenBudgetManager> budgetManager,
        SessionData? session = null)
    {
        sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
        budgetManager = new Mock<ITokenBudgetManager>(MockBehavior.Strict);

        if (session is not null)
        {
            sessionManager
                .Setup(m => m.GetOrLoadAsync(session.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);
            budgetManager
                .Setup(b => b.GetBudgetStatus(session, It.IsAny<string?>()))
                .Returns(new BudgetStatus { CurrentTokens = 120, MaxTokens = 100_000, Level = BudgetLevel.Normal });
            sessionManager
                .Setup(m => m.SaveAndNotifyAsync(session.Id, true, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        var services = new ServiceCollection();
        services.AddSingleton(sessionManager.Object);
        services.AddSingleton(budgetManager.Object);

        return new BudgetUpdateHook(services.BuildServiceProvider(), Mock.Of<ITokenCounter>());
    }
}
