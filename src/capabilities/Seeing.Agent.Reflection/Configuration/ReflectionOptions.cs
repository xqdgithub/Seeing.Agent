namespace Seeing.Agent.Reflection.Configuration;

/// <summary>
/// 复盘（反思）运行模式。
/// </summary>
public enum ReflectionMode
{
    /// <summary>完全关闭：即使 <see cref="ReflectionOptions.Enabled"/> 为真也不触发。</summary>
    Off = 0,

    /// <summary>只建议：复盘产出以建议形式呈现，不自动落库/改配置（默认，风险最低）。</summary>
    SuggestOnly = 1,

    /// <summary>允许创建：复盘可自动写入新的记忆/知识条目（仍不修改既有配置）。</summary>
    ApplyCreate = 2
}

/// <summary>
/// 自动复盘（Reflection）能力配置（配置节 <c>SeeingAgent:Reflection</c>）。
/// </summary>
public sealed class ReflectionOptions
{
    /// <summary>
    /// 总开关。<b>默认关闭，需用户显式开启</b>：自动复盘会产生额外模型调用与磁盘写入，
    /// 属于后台自主行为，必须由用户主动授权，不能默认替用户决定。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 运行模式。默认 <see cref="ReflectionMode.SuggestOnly"/>：开启后先只产出建议，
    /// 让用户观察到复盘质量后再考虑放开写入，避免一开启就污染长期记忆。
    /// </summary>
    public ReflectionMode Mode { get; set; } = ReflectionMode.SuggestOnly;

    /// <summary>
    /// 轮次触发间隔：同一会话累计完成 N 轮顶层执行后触发一次复盘。
    /// 默认 10 轮——低于 5 轮几乎没有可总结的新信息，过高则复盘滞后于用户的实际使用节奏。
    /// </summary>
    public int TurnNudgeInterval { get; set; } = 10;

    /// <summary>
    /// 空闲触发阈值（分钟）：会话空闲超过该时长后由后台 tick 触发一次复盘。
    /// 默认 30 分钟，既避开用户短暂离开后的继续对话，又能覆盖「一段工作结束」的自然断点。
    /// </summary>
    public int IdleMinutes { get; set; } = 30;

    /// <summary>
    /// 每小时最多复盘次数（全局，不区分会话）。默认 6 次——限制后台模型的成本尖峰，
    /// 同时保证正常使用强度下不会长期排队。
    /// </summary>
    public int MaxRunsPerHour { get; set; } = 6;

    /// <summary>
    /// 每日 Token 预算（全局）。默认 300,000——大致相当于十几次中等规模复盘，
    /// 超出后当日静默停跑，次日恢复（复盘不是精确计费，宁可保守也不突破用户预期）。
    /// </summary>
    public int DailyTokenBudget { get; set; } = 300_000;

    /// <summary>
    /// 单次复盘执行超时（分钟）。默认 10 分钟：复盘是只读分析，超过该时长基本意味着卡死，
    /// 需要主动放弃以免占用执行并发额度。
    /// </summary>
    public int RunTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// 单次复盘允许的最大模型步数。默认 8 步：足够完成「列证据 → 归纳 → 产出建议」，
    /// 又不至于让后台任务无限发散。
    /// </summary>
    public int MaxSteps { get; set; } = 8;

    /// <summary>
    /// 复盘使用的模型标识；<c>null</c> 表示复用会话当前模型。
    /// 默认不指定，避免在用户未配置时强行绑定某个供应商模型。
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// 复盘请求队列容量（进程内，按会话去重）。默认 32——远超同小时的触发上限，
    /// 保留余量的同时避免后台积压无界增长。
    /// </summary>
    public int QueueCapacity { get; set; } = 32;

    /// <summary>
    /// 单条工具结果的最大字符数（默认 4,000）。工具输出通常是噪声主体，
    /// 逐条截断比整篇截断更能保留「每步做了什么」的骨架。
    /// </summary>
    public int MaxToolResultChars { get; set; } = 4_000;

    /// <summary>
    /// 执行记录正文的最大字符数（默认 60,000）。
    /// 超长时从<b>尾部</b>保留：复盘更关心任务如何收尾，而不是开头的背景交代。
    /// </summary>
    public int MaxTranscriptChars { get; set; } = 60_000;
}
