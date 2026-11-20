using FluentAssertions;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

/// <summary>进程 CWD 的写入点必须受宿主开关控制（spec §5.1）。</summary>
public class ProcessCwdSwitchTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "seeing_ws_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Switch_WithMutatesProcessCwdFalse_ShouldNotChangeProcessCwd()
    {
        var before = Directory.GetCurrentDirectory();
        var provider = new WorkspaceProvider(NewRoot()) { MutatesProcessCwd = false };

        provider.SetWorkspaceRoot(NewRoot());

        Directory.GetCurrentDirectory().Should().Be(before);
    }

    [Fact]
    public void Switch_WithMutatesProcessCwdTrue_ShouldChangeProcessCwd()
    {
        var before = Directory.GetCurrentDirectory();
        var root = NewRoot();
        var provider = new WorkspaceProvider(before) { MutatesProcessCwd = true };

        try
        {
            provider.SetWorkspaceRoot(root);
            Directory.GetCurrentDirectory().Should().Be(Path.GetFullPath(root));
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
        }
    }
}
