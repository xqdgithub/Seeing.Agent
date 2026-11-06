using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 定时任务定义的深拷贝。
/// <para>
/// <c>JobEditorModal</c> 的表单直接绑定到传入的 <see cref="ScheduledJobSpec"/> 实例上，
/// 编辑过程中用户每敲一个字符都会改到那个对象。若把 <c>ScheduleManager</c> 返回的活对象
/// 直接传进弹窗，取消编辑、或保存校验失败时，未保存的修改会残留在调度器内存里
/// （下次刷新页面又「自己变回来了」）。
/// </para>
/// <para>
/// 列表页与详情页都要编辑，拷贝逻辑必须只有一份——两份各自维护迟早一边改一边忘。
/// </para>
/// </summary>
public static class ScheduledJobCloner
{
    /// <summary>深拷贝任务定义；反序列化失败时退回一个空定义，避免把 null 塞进表单。</summary>
    public static ScheduledJobSpec Clone(ScheduledJobSpec job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return System.Text.Json.JsonSerializer.Deserialize<ScheduledJobSpec>(
            System.Text.Json.JsonSerializer.Serialize(job)) ?? new ScheduledJobSpec();
    }
}
