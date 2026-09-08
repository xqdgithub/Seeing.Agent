using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Acp.Configuration;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Prompts;
using Seeing.Agent.Core.Execution;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Hosting.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// Spec §8：Options 拆节、配置节注册、提示词解耦、子执行解耦。
/// </summary>
public class ConfigPromptTaskTests
{
    [Fact]
    public void Core_Assembly_Should_Not_Contain_Gateway_Or_Acp_Options_Types()
    {
        var core = typeof(Seeing.Agent.Core.Extensions.ServiceCollectionExtensions).Assembly;
        var typeNames = core.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        typeNames.Should().NotContain("GatewayOptions");
        typeNames.Should().NotContain("AcpOptions");

        // AcpOptions 住在能力包程序集（非 Core）
        typeof(AcpOptions).Assembly.GetName().Name.Should().Contain("Acp");
        typeof(AcpOptions).Assembly.Should().NotBeSameAs(core);
    }

    [Fact]
    public void BuildSectionRegistry_Spine_Only_Capability_Sections_By_Modules_Load_Deferred()
    {
        // CreateWithSpine ≡ BuildSectionRegistry（脊柱节）
        var spine = ConfigSectionRegistry.CreateWithSpine();
        var keys = spine.Sections.Select(s => s.Key).OrderBy(k => k).ToList();

        keys.Should().Equal(
            "AgentModels",
            "DefaultAgent",
            "DefaultModel",
            "GlobalWorkspaceRoot",
            "Modules",
            "Permission",
            "PluginEnabled",
            "Plugins",
            "Providers",
            "Scenario",
            "Scenarios",
            "Seams",
            "TitleGeneration",
            "ToolOutput",
            "Workspace");

        keys.Should().NotContain("Acp");
        keys.Should().NotContain("Gateway");
        keys.Should().NotContain("Scheduler");
        keys.Should().NotContain("Memory");

        // 能力包节由模块注册到共享 registry
        spine.Register(new ConfigSectionMeta(
            AcpOptions.SectionName, "seeing.json", ConfigScope.UserOnly, typeof(AcpOptions)));
        spine.TryGet("Acp", out _).Should().BeTrue();

        // LoadAsync 推迟：factory 不在构造时同步 Load
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(Path.GetTempPath());
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(Path.GetTempPath());
        var manager = new UnifiedConfigManager(
            workspace.Object,
            NullLogger<UnifiedConfigManager>.Instance,
            ConfigSectionRegistry.CreateWithSpine());
        manager.SeeingAgent.DefaultAgent.Should().BeNull(
            "LoadAsync 推迟到 InitializeSeeingAsync，构造后尚未加载配置");
    }

    [Fact]
    public void PromptBuilder_Does_Not_Reference_SkillManager_Uses_Anchor_Injection()
    {
        var ctorParams = typeof(PromptBuilder)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        ctorParams.Should().NotContain(t => t.Name == "SkillManager" || t.Name == "ISkillManager");
        ctorParams.Should().Contain(t =>
            t.IsGenericType &&
            t.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
            t.GetGenericArguments()[0] == typeof(IPromptSectionContributor));

        // section 注入走锚点标题（不依赖 {{skills}} 占位符）— 行为由 PromptBuilderSectionInjectionTests 覆盖；
        // 此处断言内置锚点名常量存在
        PromptSectionNames.Tools.Should().Be("tools");
        PromptSectionNames.Skills.Should().Be("skills");
    }

    [Fact]
    public void TaskTool_And_TaskStatusTool_Depend_Only_On_Abstractions_Execution_Seams()
    {
        AssertTaskCtor(typeof(TaskTool));
        AssertTaskCtor(typeof(TaskStatusTool));

        // 源码级：无 context.Services / ExecutionJobService 具体类解析
        var hostingAsm = typeof(TaskTool).Assembly;
        foreach (var type in new[] { typeof(TaskTool), typeof(TaskStatusTool) })
        {
            var sourceHints = type.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .ToList();

            sourceHints.Should().NotContain(typeof(Seeing.Agent.Hosting.Execution.ExecutionJobService));
            sourceHints.Should().NotContain(t => t.Name == "ExecutionJobService");
        }

        // ToolContext.Services 不得被 Task 工具构造依赖（执行路径用注入接口）
        var taskToolSource = Path.Combine(
            FindRepoRoot(),
            "src", "Seeing.Agent.Hosting", "Tools", "TaskTool.cs");
        var taskStatusSource = Path.Combine(
            FindRepoRoot(),
            "src", "Seeing.Agent.Hosting", "Tools", "TaskStatusTool.cs");

        if (File.Exists(taskToolSource))
        {
            var text = File.ReadAllText(taskToolSource);
            text.Should().NotContain("context.Services");
            text.Should().NotContain("ExecutionJobService");
            text.Should().Contain("IExecutionSubmitter");
        }

        if (File.Exists(taskStatusSource))
        {
            var text = File.ReadAllText(taskStatusSource);
            text.Should().NotContain("context.Services");
            text.Should().NotContain("ExecutionJobService");
            text.Should().Contain("IExecutionSubmitter");
        }
    }

    private static void AssertTaskCtor(Type toolType)
    {
        var paramTypes = toolType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        paramTypes.Should().Contain(typeof(IExecutionSubmitter));
        paramTypes.Should().Contain(typeof(IExecutionStatusProvider));
        if (toolType == typeof(TaskTool))
            paramTypes.Should().Contain(typeof(IExecutionEventPublisher));

        paramTypes.Should().NotContain(t =>
            t.Namespace != null &&
            t.Namespace.StartsWith("Seeing.Agent.Hosting", StringComparison.Ordinal) &&
            t.Name.Contains("ExecutionJob", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
