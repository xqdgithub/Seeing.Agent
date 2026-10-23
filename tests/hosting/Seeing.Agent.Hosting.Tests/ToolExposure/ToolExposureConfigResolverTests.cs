#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Scenarios;
using Seeing.Agent.Hosting.Execution.ToolExposure;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.ToolExposure;

public class ToolExposureConfigResolverTests
{
    [Fact]
    public void ShouldResolvePriorityOrder()
    {
        var resolver = Create();

        // 会话 盖 Agent 盖 Scenario 盖 全局
        var sessionWins = resolver.Resolve(
            Session(ToolExposurePolicyMode.All),
            Agent(ToolExposurePolicyMode.Tiered),
            Scenario("Tiered"),
            Global("Tiered"));
        sessionWins.Mode.Should().Be(ToolExposurePolicyMode.All);

        // Agent(枚举) 盖 Scenario 盖 全局（会话未配置）
        var agentWins = resolver.Resolve(
            sessionOverride: null,
            agent: Agent(ToolExposurePolicyMode.Auto),
            scenario: Scenario("All"),
            globalOptions: Global("All"));
        agentWins.Mode.Should().Be(ToolExposurePolicyMode.Auto);

        // Scenario 盖 全局（会话/Agent 未配置）
        var scenarioWins = resolver.Resolve(
            sessionOverride: null,
            agent: null,
            scenario: Scenario("Tiered"),
            globalOptions: Global("All"));
        scenarioWins.Mode.Should().Be(ToolExposurePolicyMode.Tiered);

        // 全局兜底（上层全未配置）
        var globalWins = resolver.Resolve(
            sessionOverride: null,
            agent: null,
            scenario: null,
            globalOptions: Global("All"));
        globalWins.Mode.Should().Be(ToolExposurePolicyMode.All);
    }

    [Fact]
    public void InvalidScenarioValue_ShouldWarnAndFallBackToGlobal()
    {
        var logger = new CapturingLogger();
        var resolver = Create(logger);

        // 场景层非法字符串 "tierd" → 记 Warning 并回退到全局 "All"
        var result = resolver.Resolve(
            sessionOverride: null,
            agent: null,
            scenario: Scenario("tierd"),
            globalOptions: Global("All"));

        result.Mode.Should().Be(ToolExposurePolicyMode.All);
        logger.Warnings.Should().NotBeEmpty("非法场景值必须产生至少一次 Warning");
    }

    [Fact]
    public void AgentEnum_ShouldOverrideScenarioString()
    {
        var logger = new CapturingLogger();
        var resolver = Create(logger);

        // Agent 枚举 Auto 优先于合法场景串 "All"，故场景/全局根本不参与解析 → 无告警
        var result = resolver.Resolve(
            sessionOverride: null,
            agent: Agent(ToolExposurePolicyMode.Auto),
            scenario: Scenario("All"),
            globalOptions: Global("Tiered"));

        result.Mode.Should().Be(ToolExposurePolicyMode.Auto);
        logger.Entries.Should().BeEmpty("命中 Agent 枚举后不应解析低优先级层");
    }

    [Fact]
    public void Resolve_NoOverrides_ShouldUseDefaultPinnedTools()
    {
        var result = Create().Resolve(
            sessionOverride: null,
            agent: null,
            scenario: null,
            globalOptions: new SeeingAgentOptions());

        result.PinnedTools.Should().Equal(ToolExposureConfigResolver.DefaultPinnedTools);
    }

    [Fact]
    public void Resolve_NonEmptyConfigPinned_ShouldReplaceDefaults()
    {
        var options = new SeeingAgentOptions();
        options.Tools.Exposure.PinnedTools = ["custom_a", "custom_b"];

        var result = Create().Resolve(
            sessionOverride: null,
            agent: null,
            scenario: null,
            globalOptions: options);

        result.PinnedTools.Should().Equal("custom_a", "custom_b");
    }

    [Fact]
    public void Resolve_AgentPinnedEmpty_ShouldPinNothing()
    {
        var options = new SeeingAgentOptions();
        options.Tools.Exposure.PinnedTools = ["custom_a"];
        var agent = new AgentDefinition { Name = "a", PinnedTools = [] };

        var result = Create().Resolve(
            sessionOverride: null,
            agent: agent,
            scenario: null,
            globalOptions: options);

        result.PinnedTools.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_AgentPinnedNonEmpty_ShouldOverrideConfig()
    {
        var options = new SeeingAgentOptions();
        options.Tools.Exposure.PinnedTools = ["custom_a"];
        var agent = new AgentDefinition { Name = "a", PinnedTools = ["agent_tool"] };

        var result = Create().Resolve(
            sessionOverride: null,
            agent: agent,
            scenario: null,
            globalOptions: options);

        result.PinnedTools.Should().Equal("agent_tool");
    }

    // === helpers ===

    private static ToolExposureConfigResolver Create(ILogger<ToolExposureConfigResolver>? logger = null)
        => new(new Mock<IOptionsMonitor<SeeingAgentOptions>>().Object, logger);

    private static SessionScenarioOverride Session(ToolExposurePolicyMode mode)
        => new() { Tools = new SessionToolsOverride { PolicyMode = mode.ToString() } };

    private static AgentDefinition Agent(ToolExposurePolicyMode mode)
        => new() { Exposure = mode };

    private static ScenarioDefinition Scenario(string? policyMode)
        => new(
            Name: "test",
            Modules: Array.Empty<string>(),
            DefaultAgent: "general",
            Seams: new Dictionary<string, string>(),
            ToolsDisabled: Array.Empty<string>(),
            PolicyMode: policyMode);

    private static SeeingAgentOptions Global(string policyMode)
    {
        var options = new SeeingAgentOptions();
        options.Tools.Exposure.PolicyMode = policyMode;
        return options;
    }

    /// <summary>记录所有日志条目的测试替身（规避 Moq 对泛型 <see cref="ILogger.Log{TState}"/> 的脆弱匹配）。</summary>
    private sealed class CapturingLogger : ILogger<ToolExposureConfigResolver>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Warnings
            => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            where TState : notnull
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
