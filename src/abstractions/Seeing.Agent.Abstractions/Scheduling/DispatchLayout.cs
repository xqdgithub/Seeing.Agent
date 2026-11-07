namespace Seeing.Agent.Abstractions.Scheduling;

/// <summary>调度结果的会话写回布局。</summary>
public enum DispatchLayout
{
    /// <summary>
    /// 先写一条任务提示（<c>Presentation=System</c>，未读，下次 Wake 会随用户输入一起进入模型），
    /// 再写一条已消费的助手结果。定时任务现状。
    /// </summary>
    PromptAndResult = 0,

    /// <summary>
    /// 只写一条已消费的助手结果。心跳使用，避免在目标会话留下未读的系统输入。
    /// </summary>
    AssistantOnly = 1
}
