namespace Seeing.Agent.Core.Events;

/// <summary>
/// 将 Child Loop 事件降采样为父侧 Task* 投影事件。
/// </summary>
public interface ITaskEventProjector
{
    IEnumerable<IMessageEvent> Project(IMessageEvent childEvent, TaskProjectionContext ctx);

    TaskStartedEvent CreateStarted(TaskProjectionContext ctx, string? loopId = null);

    TaskCompletedEvent CreateCompleted(TaskProjectionContext ctx, string? resultText, TimeSpan? duration = null, string? loopId = null);

    TaskFailedEvent CreateFailed(TaskProjectionContext ctx, string error, bool cancelled = false, string? loopId = null);
}
