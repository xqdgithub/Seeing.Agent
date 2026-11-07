using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Tests.Fixtures;

/// <summary>测试用临时工作区，包含完整 Scheduler 配置</summary>
public sealed class SchedulerTestWorkspace : IDisposable
{
    public string Root { get; }
    public string SeeingDirectory { get; }
    public WorkspaceProvider Workspace { get; }
    public SchedulerOptions Options { get; set; }

    public SchedulerTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"seeing_scheduler_test_{Guid.NewGuid():N}");
        SeeingDirectory = Path.Combine(Root, ".seeing");
        Directory.CreateDirectory(SeeingDirectory);
        Workspace = new WorkspaceProvider(Root);
        Options = CreateDefaultSchedulerOptions();
    }

    public void WriteSeeingJson(SchedulerOptions? scheduler = null, string? defaultAgent = "test-agent")
    {
        scheduler ??= CreateDefaultSchedulerOptions();
        Options = scheduler;
        var json = $$"""
        {
          "SeeingAgent": {
            "DefaultAgent": "{{defaultAgent}}",
            "Scheduler": {
              "Enabled": {{scheduler.Enabled.ToString().ToLower()}},
              "Timezone": "{{scheduler.Timezone}}",
              "MaxConcurrentJobs": {{scheduler.MaxConcurrentJobs}}
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(SeeingDirectory, "seeing.json"), json);
    }

    /// <summary>创建测试用的 SchedulerOptionsProvider（简化版本，不依赖 UnifiedConfigManager）</summary>
    public TestSchedulerOptionsProvider CreateOptionsProvider()
    {
        return new TestSchedulerOptionsProvider(Options);
    }

    public static SchedulerOptions CreateDefaultSchedulerOptions() => new()
    {
        Enabled = true,
        Timezone = "UTC",
        MaxConcurrentJobs = 2
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }
}

/// <summary>测试用的 SchedulerOptionsProvider 简化实现</summary>
public sealed class TestSchedulerOptionsProvider : ISchedulerOptionsProvider
{
    private SchedulerOptions _options;

    public TestSchedulerOptionsProvider(SchedulerOptions options)
    {
        _options = options;
    }

    public SchedulerOptions Current => _options;

    public void Reload() { }

    public IDisposable OnChange(Action<SchedulerOptions> listener) => new NoopSubscription();

    public void SetOptions(SchedulerOptions options) => _options = options;

    private sealed class NoopSubscription : IDisposable
    {
        public void Dispose() { }
    }
}
