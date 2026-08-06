using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class InstanceRegistryTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"seeing-cli-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Add_Then_Load_ShouldReturnRecord()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);

        registry.Add(new InstanceRecord
        {
            Id = "webui-abc123",
            Service = "webui",
            Pid = 1234,
            WorkspaceRoot = @"D:\projA",
            Port = 5000,
            StartedAt = DateTime.UtcNow
        });

        var loaded = registry.Load();
        loaded.Should().ContainSingle();
        loaded[0].Id.Should().Be("webui-abc123");
        loaded[0].WorkspaceRoot.Should().Be(@"D:\projA");
        loaded[0].Port.Should().Be(5000);
    }

    [Fact]
    public void Remove_ShouldDeleteMatchingRecord()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);
        registry.Add(new InstanceRecord { Id = "a", Service = "webui", Pid = 1 });
        registry.Add(new InstanceRecord { Id = "b", Service = "gateway", Pid = 2 });

        registry.Remove("a");

        registry.Load().Should().ContainSingle(r => r.Id == "b");
    }

    [Fact]
    public void PruneDead_ShouldRemoveRecordsWithDeadPid()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);
        registry.Add(new InstanceRecord { Id = "dead", Service = "webui", Pid = 999999 });
        registry.Add(new InstanceRecord { Id = "alive", Service = "webui", Pid = Environment.ProcessId });

        var alive = registry.PruneDead();

        alive.Should().ContainSingle(r => r.Id == "alive");
        registry.Load().Should().ContainSingle(r => r.Id == "alive");
    }

    [Fact]
    public void Load_WhenFileMissing_ShouldReturnEmpty()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);
        registry.Load().Should().BeEmpty();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string path) { Path = path; Directory.CreateDirectory(path); }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}