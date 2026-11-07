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
    /// 只写「提示 + 结果」，且都不进 LLM 历史。
    /// <para>
    /// 提示用 <c>Presentation=None</c>：可持久化展示（用户能看到这轮心跳的指令），
    /// 但按 <c>inbox.presentation=none</c> 被排除出 LLM 历史，不在目标会话留下未读输入。
    /// </para>
    /// <para>心跳使用。定时任务用 <see cref="PromptAndResult"/>。</para>
    /// </summary>
    AssistantOnly = 1
}
