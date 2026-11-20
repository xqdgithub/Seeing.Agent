using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Agent.Abstractions.Sessions;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Hosting.Execution;
using Seeing.Session.Core;
using Seeing.Session.Management;
using System.Globalization;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

/// <summary>
/// 幂等键保留策略：<c>inbox.idem:&lt;key&gt;</c> 随每次后台活动完成增长，
/// 必须在投递预检阶段惰性收敛（24 小时保留期 + 500 条上限），否则会话 Metadata 无界膨胀。
/// <para>
/// 时钟用 <see cref="FakeTimeProvider"/> 注入（<see cref="SessionRuntime"/> 构造时从
/// 服务容器解析），过期时间戳直接写进 Metadata —— 全程不做任何真实等待。
/// </para>
/// </summary>
public class InboxIdempotencyRetentionTests
{
    private const string KeyPrefix = SessionRuntime.IdempotencyKeyPrefix;
    private const string AtPrefix = SessionRuntime.IdempotencyTimestampPrefix;

    private sealed class CompletingRunner : IExecutionRunner
    {
        public Task<ExecutionRecord> RunOnceAsync(
            ExecutionRecord record, InboxBatch batch, CancellationToken executionToken)
        {
            _ = batch;
            _ = executionToken;
            record.Status = ExecutionStatus.Completed;
            return Task.FromResult(record);
        }
    }

    private sealed class NoopPublisher : IExecutionEventPublisher
    {
        public void Publish(string sessionId, IMessageEvent evt) { }

        public IAsyncEnumerable<IMessageEvent> SubscribeAsync(
            string sessionId, CancellationToken cancellationToken) => Stream();

        private static async IAsyncEnumerable<IMessageEvent> Stream()
        {
            await Task.CompletedTask;
            yield break;
        }

        public IReadOnlyList<IMessageEvent> GetBufferedEvents(string sessionId) => [];

        public void ClearBuffer(string sessionId) { }

        public void CompleteSession(string sessionId) { }

        public ILiveEventSubscription SubscribeLive(string sessionId) => LiveEventSubscription.Completed;
    }

    private static SessionRuntime CreateRuntime(
        TimeProvider time, out SessionManager sessionManager)
    {
        sessionManager = new SessionManager(logger: NullLogger<SessionManager>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<ISessionManager>(sessionManager);
        services.AddSingleton<IBackgroundActivityControl>(new RecordingBackgroundActivityControl());
        services.AddSingleton(time);
        var provider = services.BuildServiceProvider();

        var options = new ExecutionOptions
        {
            MaxConcurrentExecutions = -1,
            SessionIdleTimeout = TimeSpan.FromMinutes(30),
            // 拉长清理间隔，避免定时器在测试期间自动触发
            CleanupInterval = TimeSpan.FromHours(1)
        };

        return new SessionRuntime(
            new CompletingRunner(), new NoopPublisher(), provider, options,
            NullLogger<SessionRuntime>.Instance);
    }

    private static InboxMessage Msg(string sessionId, string idempotencyKey) => new()
    {
        TargetSessionId = sessionId,
        Sender = new InboxSender { Kind = InboxSenderKind.System, Id = "test" },
        Body = "hello",
        Delivery = InboxDelivery.Wake,
        Presentation = InboxPresentation.User,
        IdempotencyKey = idempotencyKey
    };

    private static void Seed(SessionData session, string rawKey, long atSeconds)
    {
        session.Metadata[$"{KeyPrefix}{rawKey}"] = "seeded-message";
        session.Metadata[$"{AtPrefix}{rawKey}"] = atSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private static int CountKeys(SessionData session)
        => session.Metadata.Keys.Count(k =>
            k.StartsWith(KeyPrefix, StringComparison.Ordinal)
            && !k.StartsWith(AtPrefix, StringComparison.Ordinal));

    [Fact]
    public async Task Deliver_ShouldDropIdempotencyKeys_OlderThanRetention()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var runtime = CreateRuntime(time, out var sessionManager);
        var session = await sessionManager.EnsureSessionAsync("s1");

        // 25 小时前（过期）与 1 小时前（新鲜）
        Seed(session, "activity-digest:old", now - (long)TimeSpan.FromHours(25).TotalSeconds);
        Seed(session, "activity-digest:fresh", now - (long)TimeSpan.FromHours(1).TotalSeconds);

        await runtime.DeliverAsync(Msg("s1", "activity-digest:new"));

        session.Metadata.Should().NotContainKey($"{KeyPrefix}activity-digest:old");
        session.Metadata.Should().NotContainKey($"{AtPrefix}activity-digest:old");

        session.Metadata.Should().ContainKey($"{KeyPrefix}activity-digest:fresh");
        session.Metadata.Should().ContainKey($"{AtPrefix}activity-digest:fresh");

        // 新键与时间戳同批写入（Unix 秒）
        session.Metadata[$"{KeyPrefix}activity-digest:new"].Should().NotBeNullOrEmpty();
        session.Metadata[$"{AtPrefix}activity-digest:new"]
            .Should().Be(now.ToString(CultureInfo.InvariantCulture));

        await runtime.WaitForIdleAsync("s1");
    }

    [Fact]
    public async Task Deliver_ShouldCapIdempotencyKeys()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var runtime = CreateRuntime(time, out var sessionManager);
        var session = await sessionManager.EnsureSessionAsync("s1");

        // 600 条都在保留期内，差异只在时间戳先后：必须按「最旧优先」删到上限。
        const int seeded = 600;
        for (var i = 0; i < seeded; i++)
            Seed(session, $"activity-digest:{i}", now - (seeded - i));

        await runtime.DeliverAsync(Msg("s1", "activity-digest:newest"));

        CountKeys(session).Should().Be(SessionRuntime.IdempotencyRetentionLimit);

        // 删掉的是最旧的 101 条（600 - (500 - 1)），新键占最后一格。
        session.Metadata.Should().NotContainKey($"{KeyPrefix}activity-digest:0");
        session.Metadata.Should().NotContainKey($"{AtPrefix}activity-digest:0");
        session.Metadata.Should().NotContainKey($"{KeyPrefix}activity-digest:100");
        session.Metadata.Should().ContainKey($"{KeyPrefix}activity-digest:101");
        session.Metadata.Should().ContainKey($"{KeyPrefix}activity-digest:599");
        session.Metadata.Should().ContainKey($"{KeyPrefix}activity-digest:newest");

        await runtime.WaitForIdleAsync("s1");
    }
}
