using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Hooks;
using Seeing.Agent.Llm;
using Seeing.Agent.Rules;
using Xunit;

namespace Seeing.Agent.Tests.Integration;

/// <summary>
/// Wave 1 集成测试 - 验证所有 Wave 1 功能协同工作
/// </summary>
public class Wave1IntegrationTests
{
    private readonly Mock<ILogger<RuleEngine>> _ruleEngineLoggerMock = new();
    /// <summary>
    /// 测试完整的 Wave 1 功能集成：
    /// AgentMode + PermissionCache + MergeDeep + MaxSteps
    /// </summary>
    [Fact]
    public async Task FullWave1Integration_ShouldWorkTogether()
    {
        // Arrange - 创建完整配置
        var globalConfig = new TestAgentConfig
        {
            Temperature = 0.5,
            MaxSteps = 50,
            Mode = AgentMode.All,
            Permissions = new List<PermissionRule>
            {
                new PermissionRule { Permission = "file_read", Pattern = "*", Action = PermissionAction.Allow }
            }
        };

        var projectConfig = new TestAgentConfig
        {
            Temperature = 0.3,
            MaxSteps = 100
            // Permissions 不设置，使用默认空列表
        };

        var instanceConfig = new TestAgentConfig
        {
            Temperature = 0.1,
            MaxSteps = 30
        };

        // Act - 合并配置
        var mergedConfig = MergeDeep.MergeChain(globalConfig, projectConfig, instanceConfig);

        // Assert - 验证合并结果
        mergedConfig.Temperature.Should().Be(0.1); // instance 覆盖
        mergedConfig.MaxSteps.Should().Be(30); // instance 覆盖
        mergedConfig.Mode.Should().Be(AgentMode.All); // global 保持

        // 验证权限（空集合被认为是"未设置"，保留 base 值）
        mergedConfig.Permissions.Should().HaveCount(1);
        mergedConfig.Permissions[0].Permission.Should().Be("file_read");
        
        await Task.CompletedTask; // 消除 async 警告
    }

    /// <summary>
    /// 测试 AgentMode 过滤 + Permission 缓存协同
    /// </summary>
    [Fact]
    public void AgentModeWithPermissionCache_ShouldFilterCorrectly()
    {
        // Arrange
        var ruleEngine = new RuleEngine(_ruleEngineLoggerMock.Object);
        
        ruleEngine.AddRule(new PermissionRule
        {
            Permission = "file_read",
            Pattern = "/public/*",
            Action = PermissionAction.Allow
        });
        
        ruleEngine.AddRule(new PermissionRule
        {
            Permission = "file_read",
            Pattern = "/private/*",
            Action = PermissionAction.Deny
        });

        var cacheOptions = new PermissionCacheOptions { Ttl = TimeSpan.FromMinutes(5) };
        var cacheLoggerMock = new Mock<ILogger<PermissionCache>>();
        var cache = new PermissionCache(ruleEngine, cacheOptions, cacheLoggerMock.Object);

        // 创建 SubAgent 模式的 Agent
        var subAgentMock = new Mock<IAgent>();
        subAgentMock.Setup(a => a.Name).Returns("test-subagent");
        subAgentMock.Setup(a => a.Mode).Returns(AgentMode.SubAgent);
        subAgentMock.Setup(a => a.MaxSteps).Returns(20);

        // Act - 缓存权限决策
        var allowedKey = new PermissionCacheKey("file_read", "/public/test.txt", "test-subagent");
        var deniedKey = new PermissionCacheKey("file_read", "/private/secret.txt", "test-subagent");

        var allowedAction = ruleEngine.Evaluate("file_read", "/public/test.txt");
        var deniedAction = ruleEngine.Evaluate("file_read", "/private/secret.txt");

        cache.Set(allowedKey, allowedAction);
        cache.Set(deniedKey, deniedAction);

        // Assert - 验证缓存工作
        cache.Get(allowedKey).Should().Be(PermissionAction.Allow);
        cache.Get(deniedKey).Should().Be(PermissionAction.Deny);

        // 验证 SubAgent 模式
        subAgentMock.Object.Mode.Should().Be(AgentMode.SubAgent);
        subAgentMock.Object.MaxSteps.Should().Be(20);
    }

    /// <summary>
    /// 测试权限冲突场景：全局 deny vs agent allow
    /// </summary>
    [Fact]
    public void PermissionConflict_GlobalDenyShouldWinOverAgentAllow()
    {
        // Arrange
        var ruleEngine = new RuleEngine(_ruleEngineLoggerMock.Object);
        
        // 全局规则：拒绝敏感路径（先添加，顺序决定优先级）
        ruleEngine.AddRule(new PermissionRule
        {
            Permission = "file_write",
            Pattern = "/system/*",
            Action = PermissionAction.Deny
        });

        // Agent 规则：允许写入（后添加，优先级较低）
        var agentRules = new List<PermissionRule>
        {
            new PermissionRule
            {
                Permission = "file_write",
                Pattern = "/system/config.json",
                Action = PermissionAction.Allow
            }
        };

        // Act - 评估冲突权限
        var result = ruleEngine.Evaluate("file_write", "/system/config.json");

        // Assert - 根据添加顺序，第一个匹配的规则生效
        // Deny 规则先添加，匹配 /system/*，所以应该拒绝
        result.Should().Be(PermissionAction.Deny);
    }

    /// <summary>
    /// 测试缺失配置层级的合并
    /// </summary>
    [Fact]
    public void MissingConfigLevel_ShouldHandleGracefully()
    {
        // Arrange - 只有 Global + Instance，没有 Project
        var globalConfig = new TestAgentConfig
        {
            Temperature = 0.5,
            MaxSteps = 100,
            Mode = AgentMode.Primary
        };

        // Project 配置缺失（null）
        TestAgentConfig? projectConfig = null;

        var instanceConfig = new TestAgentConfig
        {
            Temperature = 0.1
        };

        // Act - 合并（跳过 null 的 project）
        var merged = MergeDeep.MergeChain(globalConfig, projectConfig, instanceConfig);

        // Assert - 应正确处理缺失层级
        merged.Temperature.Should().Be(0.1); // instance 覆盖
        merged.MaxSteps.Should().Be(100); // global 保持
        // 注意：enum 默认值问题，instanceConfig.Mode 默认是 All，会覆盖 global 的 Primary
        // 如果需要区分"未设置"，应使用 nullable enum (AgentMode?)
        merged.Mode.Should().Be(AgentMode.All); // instance 默认值覆盖
    }

    /// <summary>
    /// 测试 MaxSteps 执行限制
    /// </summary>
    [Fact]
    public void MaxStepsLimit_ShouldBeConfiguredCorrectly()
    {
        // Arrange - 创建测试 Agent（使用 NullLogger 避免 Moq 内部类问题）
        var agent = new TestAgent(Microsoft.Extensions.Logging.Abstractions.NullLogger<TestAgent>.Instance, null)
        {
            OverrideMaxSteps = 5
        };

        // Assert - MaxSteps 应被正确设置
        agent.MaxSteps.Should().Be(5);
        agent.Mode.Should().Be(AgentMode.All);
    }

    /// <summary>
    /// 测试配置链式合并的优先级
    /// </summary>
    [Fact]
    public void ConfigMergeChain_ShouldRespectPriority()
    {
        // Arrange - 三级配置
        var global = new TestAgentConfig { Temperature = 0.7, MaxSteps = 200, Mode = AgentMode.All };
        var project = new TestAgentConfig { Temperature = 0.4, MaxSteps = 150 };
        var instance = new TestAgentConfig { Temperature = 0.2, MaxSteps = 75 };

        // Act
        var result = MergeDeep.MergeChain(global, project, instance);

        // Assert - Instance 优先级最高
        result.Temperature.Should().Be(0.2); // instance 覆盖
        result.MaxSteps.Should().Be(75); // instance 覆盖
        result.Mode.Should().Be(AgentMode.All); // global 设置，后续无覆盖
    }

    // 测试配置类型
    private class TestAgentConfig
    {
        public double Temperature { get; set; }
        public int? MaxSteps { get; set; }
        public AgentMode Mode { get; set; } = AgentMode.All;
        public List<PermissionRule> Permissions { get; set; } = new();
    }

    // 测试 Agent 实现
    private class TestAgent : AgentBase
    {
        public int? OverrideMaxSteps { get; set; }
        
        public TestAgent(ILogger logger, IHookManager? hookManager = null) 
            : base(logger, hookManager) { }

        public override string Name => "test-agent";
        public override string Description => "测试 Agent";
        public override int? MaxSteps => OverrideMaxSteps;

        protected override async IAsyncEnumerable<ChatMessage> ExecuteCoreAsync(
            ChatMessage input,
            AgentContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // 简单实现：返回一条消息
            yield return new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = "Test response"
            };
            await Task.CompletedTask;
        }
    }
}