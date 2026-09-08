using Quartz;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>
/// JobDataMap 转换器 - 在业务对象和 JobDataMap 之间转换
/// 自动处理 UseProperties=true 的字符串存储要求
/// </summary>
public static class JobDataMapConverter
{
    /// <summary>将 ScheduledJobSpec 转换为 JobDataMap（存储时使用）</summary>
    public static JobDataMap ToJobDataMap(ScheduledJobSpec job)
    {
        var map = new JobDataMap();

        // 基本信息
        map.SetStringValue(JobDataKeys.JobId, job.Id);
        map.SetStringValue(JobDataKeys.JobName, job.Name);
        map.SetStringValue(JobDataKeys.TaskType, job.TaskType);
        map.SetStringValue(JobDataKeys.SessionId, job.Dispatch.Target.SessionId ?? "main");

        // 运行时配置（自动转换为字符串）
        map.SetIntValue(JobDataKeys.TimeoutSeconds, job.Runtime.TimeoutSeconds);
        map.SetBoolValue(JobDataKeys.RunInBackground, job.Runtime.RunInBackground);
        map.SetIntValue(JobDataKeys.MisfireGraceSeconds, job.Runtime.MisfireGraceSeconds);
        map.SetBoolValue(JobDataKeys.ShareSession, job.Runtime.ShareSession);

        // 任务内容
        if (job.TaskType == ScheduleTaskTypes.Text)
        {
            map.SetStringValue(JobDataKeys.Text, job.Text);
        }
        else
        {
            map.SetStringValue(JobDataKeys.AgentId, job.Agent);
            map.SetStringValue(JobDataKeys.Prompt, job.Prompt);
        }

        // 投递配置
        if (!string.IsNullOrEmpty(job.Dispatch.Target.SessionId))
            map.SetStringValue(JobDataKeys.DispatchSessionId, job.Dispatch.Target.SessionId);

        if (job.Dispatch.Meta != null && job.Dispatch.Meta.Count > 0)
            map.SetJsonValue(JobDataKeys.DispatchMeta, job.Dispatch.Meta);

        return map;
    }

    /// <summary>将 HeartbeatOptions 转换为 JobDataMap（存储时使用）</summary>
    public static JobDataMap ToJobDataMap(HeartbeatOptions heartbeat, string? agentId = null)
    {
        var map = new JobDataMap();

        map.SetStringValue(JobDataKeys.JobId, SchedulerConstants.HeartbeatJobId);
        map.SetStringValue(JobDataKeys.SessionId, heartbeat.SessionId);
        map.SetIntValue(JobDataKeys.TimeoutSeconds, heartbeat.TimeoutSeconds);
        map.SetStringValue(JobDataKeys.AgentId, agentId ?? heartbeat.Agent);
        map.SetStringValue(JobDataKeys.Prompt, heartbeat.Prompt);
        map.SetStringValue(JobDataKeys.HeartbeatTarget, heartbeat.Target);
        map.SetStringValue(JobDataKeys.Source, ScheduleSources.Heartbeat);

        if (heartbeat.ActiveHours != null)
            map.SetJsonValue(JobDataKeys.ActiveHours, heartbeat.ActiveHours);

        return map;
    }

    /// <summary>从 JobDataMap 读取 JobRuntimeSpec（读取时使用）</summary>
    public static JobRuntimeSpec GetRuntimeSpec(JobDataMap map)
    {
        return new JobRuntimeSpec
        {
            TimeoutSeconds = map.GetIntValue(JobDataKeys.TimeoutSeconds, SchedulerConstants.DefaultTimeoutSeconds),
            RunInBackground = map.GetBoolValue(JobDataKeys.RunInBackground),
            MisfireGraceSeconds = map.GetIntValue(JobDataKeys.MisfireGraceSeconds, SchedulerConstants.DefaultMisfireGraceSeconds),
            ShareSession = map.GetBoolValue(JobDataKeys.ShareSession)
        };
    }

    /// <summary>从 JobDataMap 读取 DispatchSpec（读取时使用）</summary>
    public static DispatchSpec GetDispatchSpec(JobDataMap map)
    {
        var spec = new DispatchSpec
        {
            Target = new DispatchTarget
            {
                SessionId = map.GetStringValue(JobDataKeys.DispatchSessionId)
            }
        };

        var meta = map.GetJsonValue<Dictionary<string, object>>(JobDataKeys.DispatchMeta);
        if (meta != null)
            spec.Meta = meta;

        return spec;
    }
}
