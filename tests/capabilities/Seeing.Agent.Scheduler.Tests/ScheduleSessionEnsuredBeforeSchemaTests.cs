using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Scheduler.Jobs;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>
/// 心跳/定时任务指向「尚未创建的会话」时必须先建会话再结算工具视图。
/// <para>
/// 现场症状：心跳会话里模型把工具调用当文本吐出来
/// （<c>&lt;tool calls&gt; &lt;invoke name="bash"&gt;…</c>）。
/// 根因：<see cref="ISessionToolSchemaResolver.ResolveAsync"/> 对不存在的会话返回 Empty，
/// 而旁路只对「仅 conversation」路径建会话，纯会话 id（如 main）不建。
/// 结果 LLM 请求 Tools=null，模型只能凭训练记忆「表演」工具调用。
/// </para>
/// </summary>
public class ScheduleSessionEnsuredBeforeSchemaTests
{
    [Fact]
    public async Task ResolveToolSchemas_WhenTargetSessionMissing_ShouldEnsureSessionFirst()
    {
        // Arrange：目标会话不存在
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.TryGetOrLoadAsync("hb-target", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionData?)null);
        sessionManager
            .Setup(m => m.EnsureSessionAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<SessionOrigin?>()))
            .ReturnsAsync(new SessionData { Id = "hb-target" });

        var resolver = new Mock<ISessionToolSchemaResolver>();
        resolver
            .Setup(r => r.ResolveAsync("hb-target", It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionToolSchemaResolution.Empty);

        var services = new ServiceCollection()
            .AddSingleton(sessionManager.Object)
            .AddSingleton(resolver.Object)
            .BuildServiceProvider();

        // Act
        await AgentJob.ResolveToolSchemasAsync(
            services,
            "hb-target",
            new AgentDefinition { Name = "build" },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        // Assert：结算前必须保证会话存在，否则 resolver 拿不到结算快照 → 空工具集
        sessionManager.Verify(
            m => m.EnsureSessionAsync(
                "hb-target",
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<SessionOrigin?>()),
            Times.Once,
            "会话不存在时工具结算必然为空，模型会退化成用文本「表演」工具调用");
    }

    [Fact]
    public async Task ResolveToolSchemas_WhenSessionAlreadyExists_ShouldNotRecreate()
    {
        var sessionManager = new Mock<ISessionManager>();
        sessionManager
            .Setup(m => m.TryGetOrLoadAsync("hb-target", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionData { Id = "hb-target" });

        var resolver = new Mock<ISessionToolSchemaResolver>();
        resolver
            .Setup(r => r.ResolveAsync("hb-target", It.IsAny<AgentDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionToolSchemaResolution.Empty);

        var services = new ServiceCollection()
            .AddSingleton(sessionManager.Object)
            .AddSingleton(resolver.Object)
            .BuildServiceProvider();

        await AgentJob.ResolveToolSchemasAsync(
            services,
            "hb-target",
            new AgentDefinition { Name = "build" },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        sessionManager.Verify(
            m => m.EnsureSessionAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<SessionOrigin?>()),
            Times.Never,
            "已存在的会话不应被重新创建（也不该改写其来源）");
    }
}
