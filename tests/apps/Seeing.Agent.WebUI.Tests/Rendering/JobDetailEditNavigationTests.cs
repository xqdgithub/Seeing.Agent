using FluentAssertions;
using Seeing.Agent.WebUI.Services;
using System.Text.RegularExpressions;

namespace Seeing.Agent.WebUI.Tests.Rendering;

/// <summary>
/// 任务详情页「编辑」入口的守门。
/// <para>
/// 原 bug：详情页的「编辑」按钮跳转 <c>/cron-jobs/{JobId}/edit</c>，
/// 而这条路由从未登记（<see cref="ModulePageRouteBinder.KnownModuleRoutes"/> 只有
/// <c>/cron-jobs</c> 与 <c>/cron-jobs/{JobId}</c>）。
/// <c>ModuleRouter</c> 解析不到就落到 <c>Fallback</c> 分支，
/// 渲染 <c>NotEnabledFragment</c>，于是显示「当前场景未启用『页面』」——
/// 用户看到的是「点了编辑被踢到一个空白提示页」，而真实原因只是路由不存在。
/// </para>
/// <para>
/// 列表页的编辑一直是就地弹窗（<c>JobEditorModal</c>），详情页理应与之一致，
/// 不该有第二条编辑路径。以下断言把这个约定钉住。
/// </para>
/// </summary>
public class JobDetailEditNavigationTests
{
    private const string DetailPagePath = "Pages/JobDetailPage.razor";

    [Fact]
    public void EditSubRoute_ShouldNotBeRegistered()
    {
        // 钉住事实：详情路由不接受多一层 /edit 段，所以任何指向它的跳转都会掉进 Fallback。
        // 这条断言的作用是：如果将来真加了 /cron-jobs/{JobId}/edit 路由，这里会失败，
        // 提示作者重新审视详情页是否还需要就地编辑弹窗。
        ModuleRouteResolver.TryMatchTemplate("/cron-jobs/{JobId}", "/cron-jobs/job_abc/edit", out _, out _)
            .Should().BeFalse("详情路由只有一段 JobId，多出的 /edit 段匹配不上");

        ModulePageRouteBinder.TryMatchKnown("/cron-jobs/job_abc/edit", out _, out _)
            .Should().BeFalse("已知模块路由表里也没有编辑子路由");
    }

    [Fact]
    public void DetailPage_ShouldEditInPlaceInsteadOfNavigating()
    {
        var source = ReadWebUiSource(DetailPagePath);

        // 禁止任何指向 .../edit 的跳转（就地编辑，不新增页面）
        Regex.IsMatch(source, @"NavigateTo\(\s*\$?""[^""]*/edit""", RegexOptions.CultureInvariant)
            .Should().BeFalse("详情页不得跳转到不存在的编辑路由，否则会落到「当前场景未启用」提示页");

        // 必须复用列表页同一个编辑弹窗，避免两套编辑表单各自漂移
        source.Should().Contain("<JobEditorModal",
            "详情页的编辑应复用列表页的 JobEditorModal 就地弹窗");

        // 弹窗必须接上保存回调，否则点确定没有任何效果
        source.Should().Contain("OnSave=",
            "JobEditorModal 需要 OnSave 回调把修改写回 ScheduleManager");
    }

    [Fact]
    public void BothPages_ShouldShareSameJobCloneHelper()
    {
        // 编辑前必须深拷贝：JobEditorModal 直接改 Job 实例的字段，
        // 传入 ScheduleManager 的活对象会把未保存的编辑内容泄漏进内存缓存。
        // 列表页与详情页若各写一份拷贝逻辑，迟早一边改一边忘，因此要求共用同一个 helper。
        ReadWebUiSource("Pages/CronJobsPage.razor")
            .Should().Contain("ScheduledJobCloner.Clone",
                "列表页应使用共享的拷贝 helper");

        ReadWebUiSource(DetailPagePath)
            .Should().Contain("ScheduledJobCloner.Clone",
                "详情页应使用共享的拷贝 helper");
    }

    /// <summary>
    /// 读取 WebUI 源码。测试运行时输出目录不含源码，需要回溯到仓库内的 samples 目录。
    /// </summary>
    private static string ReadWebUiSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "samples", "Seeing.Agent.WebUI", relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 WebUI 源文件：{relativePath}");
    }
}
