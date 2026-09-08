using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tests.Invariants;

/// <summary>
/// Host Shape：Hosting.Gateway 不得 ProjectReference 能力/集成包（含 Seeing.Agent.Gateway / Scheduler）。
/// </summary>
public class HostingGatewayNoSchedulerTests
{
    [Fact]
    public void Hosting_Gateway_Transitive_ProjectReferences_Should_Not_Include_Capability_Packages()
    {
        var root = FindRepoRoot();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(Path.Combine(root, "src", "Seeing.Agent.Hosting.Gateway", "Seeing.Agent.Hosting.Gateway.csproj"));

        var projectNames = new List<string>();
        while (queue.Count > 0)
        {
            var csproj = queue.Dequeue();
            if (!visited.Add(csproj) || !File.Exists(csproj))
                continue;

            projectNames.Add(Path.GetFileNameWithoutExtension(csproj));
            var dir = Path.GetDirectoryName(csproj)!;
            foreach (var line in File.ReadAllLines(csproj))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("<ProjectReference", StringComparison.OrdinalIgnoreCase))
                    continue;
                var includeIdx = trimmed.IndexOf("Include=\"", StringComparison.OrdinalIgnoreCase);
                if (includeIdx < 0)
                    continue;
                var start = includeIdx + "Include=\"".Length;
                var end = trimmed.IndexOf('"', start);
                if (end < 0)
                    continue;
                var relative = trimmed[start..end];
                queue.Enqueue(Path.GetFullPath(Path.Combine(dir, relative)));
            }
        }

        projectNames.Should().Contain("Seeing.Agent.Hosting.Gateway");
        projectNames.Should().NotContain("Seeing.Agent.Gateway",
            "Hosting.Gateway 不得引用 Seeing.Agent.Gateway；由 sample 显式 AddSeeingGatewayServer 组合");
        projectNames.Should().NotContain("Seeing.Agent.Scheduler",
            "Hosting.Gateway 不得传递引用 Scheduler；cron 由 sample 单独 AddSeeingScheduler");
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
