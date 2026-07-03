using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Tests.Fixtures;

/// <summary>测试用临时工作区，包含完整 Scheduler 配置</summary>
public sealed class SchedulerTestWorkspace : IDisposable
{
    public string Root { get; }
    public string SeeingDirectory { get; }
    public WorkspaceProvider Workspace { get; }

    public SchedulerTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"seeing_scheduler_test_{Guid.NewGuid():N}");
        SeeingDirectory = Path.Combine(Root, ".seeing");
        Directory.CreateDirectory(SeeingDirectory);
        Workspace = new WorkspaceProvider(Root);
    }

    public void WriteSeeingJson(SchedulerOptions? scheduler = null, string? defaultAgent = "test-agent")
    {
        scheduler ??= CreateDefaultSchedulerOptions();
        var json = $$"""
        {
          "SeeingAgent": {
            "DefaultAgent": "{{defaultAgent}}",
            "Scheduler": {
              "Enabled": {{scheduler.Enabled.ToString().ToLower()}},
              "Timezone": "{{scheduler.Timezone}}",
              "TickIntervalSeconds": {{scheduler.TickIntervalSeconds}},
              "MaxConcurrentJobs": {{scheduler.MaxConcurrentJobs}},
              "Heartbeat": {
                "Enabled": {{scheduler.Heartbeat.Enabled.ToString().ToLower()}},
                "Every": "{{scheduler.Heartbeat.Every}}",
                "Target": "{{scheduler.Heartbeat.Target}}",
                "QueryFile": "{{scheduler.Heartbeat.QueryFile}}",
                "Agent": {{(scheduler.Heartbeat.Agent == null ? "null" : $"\"{scheduler.Heartbeat.Agent}\"")}},
                "SessionId": "{{scheduler.Heartbeat.SessionId}}",
                "TimeoutSeconds": {{scheduler.Heartbeat.TimeoutSeconds}}
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(SeeingDirectory, "seeing.json"), json);
    }

    public void WriteHeartbeatFile(string content)
    {
        File.WriteAllText(Path.Combine(Root, "HEARTBEAT.md"), content);
    }

    public SchedulerOptionsProvider CreateOptionsProvider()
    {
        var provider = new SchedulerOptionsProvider(Workspace, NullLogger<SchedulerOptionsProvider>.Instance);
        provider.Reload();
        return provider;
    }

    public static SchedulerOptions CreateDefaultSchedulerOptions() => new()
    {
        Enabled = true,
        Timezone = "UTC",
        TickIntervalSeconds = 1,
        MaxConcurrentJobs = 2,
        Heartbeat = new HeartbeatOptions
        {
            Enabled = true,
            Every = "6h",
            Target = HeartbeatTargets.Main,
            QueryFile = "HEARTBEAT.md",
            SessionId = "main",
            TimeoutSeconds = 30
        }
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
