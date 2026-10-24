namespace Seeing.Agent.Abstractions.Context;

/// <summary>
/// 一类上下文内容的提供者：负责读取值，并提供两段渲染（初始渲染 / 增量叙述）。
/// <para>实现须保证：同一值重复渲染逐字节相等；<see cref="Render"/> 返回 null 表示本次不推进叙述。</para>
/// </summary>
public interface IContextSource
{
    /// <summary>稳定标识（约定取实现类型全名）。</summary>
    ContextSourceKey Key { get; }

    /// <summary>生效层级。</summary>
    ContextSourceScope Scope { get; }

    /// <summary>值结构版本；变化时该源基线整体重建。</summary>
    int ValueSchemaVersion { get; }

    /// <summary>初始渲染落点。</summary>
    ContextSourceTarget Target { get; }

    /// <summary>
    /// 基线文本的字符上限（0 = 不限）。<b>仅用于诊断</b>：截断由源在渲染内部完成
    /// （截断结果即存库文本，见 <c>docs/architecture/21</c> §2.4），框架只观测并在超限时记入 Issues，不再二次截断。
    /// </summary>
    int MaxBaselineChars { get; }

    /// <summary>
    /// 读取当前值。暂时不可读时须返回 <see cref="ContextValueState.Unavailable"/>，不得因可恢复的 IO 失败抛异常。
    /// </summary>
    Task<ContextSourceValue> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// 初始渲染（首次进入提示词，或 <see cref="ContextSourceTarget.Message"/> 重新可见时）。
    /// 须为纯函数：同一值逐字节稳定。
    /// </summary>
    string RenderInitial(ContextSourceValue value);

    /// <summary>
    /// 增量渲染：描述 previous → current 的变化。返回 null 表示本次不叙述（不推进 currentValues）。
    /// </summary>
    string? Render(ContextSourceValue previous, ContextSourceValue current);
}
