using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Configuration;
using Seeing.Agent.Extensions;
using Seeing.Agent.Tools.Basic;
using Seeing.Agent.Tools.FileSystem;
using Seeing.Agent.Tools.Git;
using Seeing.Agent.Tools.Shell;
using Seeing.Agent.Tools.Web;
using Seeing.IO.Local;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// Spec §8：执行世界成对；工具程序集无 File.WriteAllText / Process.Start 生产路径。
/// </summary>
public class ExecutionWorldTests
{
    [Fact]
    public void Resolved_IFileSystem_And_ISubprocess_Belong_To_Same_IExecutionWorld()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var registry = new ConfigSectionRegistry();
        services.AddSingleton<IConfigSectionRegistry>(registry);
        services.AddSeeingCore(registry);

        using var sp = services.BuildServiceProvider();
        var world = sp.GetRequiredService<IExecutionWorld>();
        var fs = sp.GetRequiredService<IFileSystem>();
        var subprocess = sp.GetRequiredService<ISubprocessFactory>();

        ReferenceEquals(fs, world.FileSystem).Should().BeTrue();
        ReferenceEquals(subprocess, world.Subprocess).Should().BeTrue();
        world.Should().BeOfType<LocalExecutionWorld>();
    }

    [Fact]
    public void Tool_Assemblies_Should_Not_Call_File_WriteAllText_Or_Process_Start()
    {
        var root = FindRepoRoot();
        var cleanDirs = new[]
        {
            Path.Combine(root, "src", "Seeing.Agent.Tools.FileSystem"),
            Path.Combine(root, "src", "Seeing.Agent.Tools.Git"),
            Path.Combine(root, "src", "Seeing.Agent.Tools.Basic"),
            Path.Combine(root, "src", "Seeing.Agent.Tools.Web"),
        };

        foreach (var dir in cleanDirs)
        {
            var hits = ScanSourceForForbiddenOsCalls(dir, allowlist: null);
            hits.Should().BeEmpty(
                "工具源码 {0} 不得直接调用 File.WriteAllText / Process.Start；命中: {1}",
                Path.GetFileName(dir),
                string.Join("; ", hits));
        }

        var shellDir = Path.Combine(root, "src", "Seeing.Agent.Tools.Shell");
        var shellHits = ScanSourceForForbiddenOsCalls(
            shellDir,
            allowlist: ["DefaultShellService.cs", "ProcessExtensions.cs"]);

        shellHits.Should().BeEmpty(
            "Shell 仅允许 DefaultShellService/ProcessExtensions 残留 Process.Start；其它命中: {0}",
            string.Join("; ", shellHits));

        // 锚定程序集仍可加载（防空引用）
        _ = typeof(ReadTool);
        _ = typeof(GitStatusTool);
        _ = typeof(CurrentTimeTool);
        _ = typeof(WebFetchTool);
        _ = typeof(BashTool);
    }

    private static List<string> ScanSourceForForbiddenOsCalls(string directory, string[]? allowlist)
    {
        var hits = new List<string>();
        if (!Directory.Exists(directory))
            return hits;

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (allowlist != null &&
                allowlist.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("File.WriteAllText", StringComparison.Ordinal) ||
                text.Contains("File.WriteAllBytes", StringComparison.Ordinal) ||
                text.Contains("Process.Start", StringComparison.Ordinal))
            {
                hits.Add(Path.GetRelativePath(directory, file));
            }
        }

        return hits;
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
