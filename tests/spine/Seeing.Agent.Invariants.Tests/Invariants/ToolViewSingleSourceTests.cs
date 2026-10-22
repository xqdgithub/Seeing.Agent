using FluentAssertions;
using System.Text.RegularExpressions;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Invariants;

/// <summary>
/// 工具视图单源不变量（源码级锁定）。
/// <para>
/// 背景：分层暴露要求"本轮模型能看到哪些工具"只有一个计算处——Hosting 的结算端口。
/// 任何在别处按集合生成 schema、或绕过端口直接枚举工具注册表判定可用性的实现，
/// 都会产生第二个真相源，导致分层暴露与实际可调集不一致。
/// </para>
/// </summary>
public class ToolViewSingleSourceTests
{
    /// <summary>结算端口的唯一实现与唯一纯函数引擎。</summary>
    private static readonly string[] s_allowedSchemaProducers =
    [
        Path.Combine("src", "hosting", "Seeing.Agent.Hosting", "Execution", "SessionToolSchemaResolver.cs"),
        Path.Combine("src", "hosting", "Seeing.Agent.Hosting", "Execution", "ToolExposure", "ToolExposureEngine.cs"),
        Path.Combine("src", "spine", "Seeing.Agent.Core", "Tools", "ToolManager.cs"),
    ];

    [Fact]
    public void Only_Settlement_Port_Should_Produce_ToolSchema_Collections()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles(Path.Combine(root, "src")))
        {
            var relative = Path.GetRelativePath(root, file);
            if (s_allowedSchemaProducers.Any(allowed =>
                    string.Equals(allowed, relative, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            // 直接构造 FunctionToolSchema 集合 = 第二条 schema 生产路径
            if (Regex.IsMatch(text, @"new\s+FunctionToolSchema\b"))
                offenders.Add(relative);
        }

        offenders.Should().BeEmpty(
            "工具 schema 只能由结算端口（SessionToolSchemaResolver / ToolExposureEngine）与 " +
            "ToolManager（按 settled 集生成）产生；其它位置构造 schema 会形成第二个真相源");
    }

    [Fact]
    public void No_One_Should_Enumerate_Tool_Registry_To_Decide_Availability_Outside_Resolver()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        var allowed = new[]
        {
            // 管理视图（配置页 / 命令）：展示注册表全量，不是"可用性判定"
            Path.Combine("src", "spine", "Seeing.Agent.Core", "Tools", "ToolManager.cs"),
            Path.Combine("src", "hosting", "Seeing.Agent.Hosting", "Execution", "SessionToolSchemaResolver.cs"),
            Path.Combine("src", "hosting", "Seeing.Agent.Hosting", "Execution", "ToolExposure", "ToolExposureEngine.cs"),
            // 契约声明本身（接口成员签名），非调用
            Path.Combine("src", "abstractions", "Seeing.Agent.Abstractions", "Tools", "IToolManager.cs"),
        };

        foreach (var file in EnumerateSourceFiles(Path.Combine(root, "src")))
        {
            var relative = Path.GetRelativePath(root, file);
            if (allowed.Any(a => string.Equals(a, relative, StringComparison.OrdinalIgnoreCase)))
                continue;

            var text = File.ReadAllText(file);
            // GetRegisteredTools() 是管理视图；若用于决定"本轮可用工具"即为第二真相源。
            // 注释/文档里的提及不算调用：只匹配实际调用点。
            if (Regex.IsMatch(text, @"GetRegisteredTools\s*\(\s*\)") &&
                !Regex.IsMatch(relative, @"(ToolsCommand|ToolsPage|McpStateService|\.md$)"))
            {
                offenders.Add(relative);
            }
        }

        offenders.Should().BeEmpty(
            "GetRegisteredTools() 只服务管理视图；用它判定本轮可用工具会绕过结算端口");
    }

    [Fact]
    public void Session_Exposure_State_Should_Only_Be_Written_At_ExecutionRunner()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        var allowed = new[]
        {
            Path.Combine("src", "primitives", "Seeing.Session", "Core", "SessionToolExposureState.cs"),
            Path.Combine("src", "hosting", "Seeing.Agent.Hosting", "Execution", "ExecutionRunner.cs"),
            Path.Combine("src", "hosting", "Seeing.Agent.Hosting", "Execution", "ToolExposure", "SessionToolDiscoveryStore.cs"),
            Path.Combine("src", "primitives", "Seeing.Session", "Core", "SessionData.cs"),
            Path.Combine("src", "primitives", "Seeing.Session", "Management", "SessionForker.cs"),
        };

        foreach (var file in EnumerateSourceFiles(Path.Combine(root, "src")))
        {
            var relative = Path.GetRelativePath(root, file);
            if (allowed.Any(a => string.Equals(a, relative, StringComparison.OrdinalIgnoreCase)))
                continue;

            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"\.ToolExposure\.(Commit|PromotePending|AddPending|ClearMetrics)\s*\("))
                offenders.Add(relative);
        }

        offenders.Should().BeEmpty(
            "会话暴露状态的提交与提升只允许在 ExecutionRunner 轮首与发现态写入端口发生");
    }

    [Fact]
    public void Scheduler_Bypass_Should_Not_Commit_Exposure_Metrics()
    {
        var root = FindRepoRoot();
        var schedulerDir = Path.Combine(root, "src", "capabilities", "Seeing.Agent.Scheduler");

        foreach (var file in EnumerateSourceFiles(schedulerDir))
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain(".ToolExposure.Commit(",
                "Scheduler 是旁路执行，只读视图并提升发现，不得提交度量与升级位");
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（Seeing.Agent.slnx）");
    }
}
