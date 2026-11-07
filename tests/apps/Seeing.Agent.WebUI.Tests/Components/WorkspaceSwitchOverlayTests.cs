using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.WebUI.Components;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;
/// <summary>
/// 工作区切换进度层的渲染契约。
/// <para>
/// 组件本身只在 <see cref="WorkspaceSwitchProgress.IsRunning"/> 为真时输出遮罩；
/// 这里用真实的 Blazor 渲染器把「未开始 / 进行中 / 结束后」三种状态各渲染一次，
/// 确认覆盖层确实随切换出现与消失（而不是只有 CSS 存在）。
/// </para>
/// </summary>
public class WorkspaceSwitchOverlayTests
{
    [Fact]
    public void Overlay_Should_Render_Only_WhileSwitching()
    {
        var progress = new WorkspaceSwitchProgress();
        using var renderer = new TestRenderer(progress);

        var markup = renderer.Render();

        markup.Should().NotContain("ws-progress-overlay", "未切换时不应有遮罩");
    }

    [Fact]
    public void Overlay_Should_Show_CurrentStep_WhileSwitching()
    {
        var progress = new WorkspaceSwitchProgress();
        using var renderer = new TestRenderer(progress);

        progress.Begin("正在切换工作区…");
        var started = renderer.Render();

        started.Should().Contain("ws-progress-overlay");
        started.Should().Contain("ws-progress-step");
        ContainsText(started, "正在切换工作区…").Should().BeTrue(
            "覆盖层要显示切换器上报的当前步骤");

        progress.Report("正在重载会话与模块…");
        var reporting = renderer.Render();

        ContainsText(reporting, "正在重载会话与模块…").Should().BeTrue("上报新步骤后应更新显示");

        progress.End();
        var ended = renderer.Render();

        ended.Should().NotContain("ws-progress-overlay", "切换结束后遮罩必须消失");
    }

    /// <summary>
    /// 断言渲染结果包含某段文字。
    /// <para>
    /// Razor 会把中文输出为 HTML 实体（且可能是十六进制），因此不能直接比原字符串；
    /// 这里同时接受原字符串与实体编码两种形式，避免把断言绑死在编码细节上。
    /// </para>
    /// </summary>
    private static bool ContainsText(string markup, string text)
    {
        if (markup.Contains(text, StringComparison.Ordinal))
            return true;

        return markup.Contains(System.Net.WebUtility.HtmlEncode(text), StringComparison.Ordinal)
               || markup.Contains(ToHexEntities(text), StringComparison.Ordinal);
    }

    private static string ToHexEntities(string text) =>
        string.Concat(text.Select(c => c < 128 ? c.ToString() : $"&#x{(int)c:X};"));

    /// <summary>最小渲染器：只渲这一个组件，取 HTML 断言。</summary>
    private sealed class TestRenderer : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HtmlRenderer _renderer;

        public TestRenderer(WorkspaceSwitchProgress progress)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton(progress);
            collection.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            _services = collection.BuildServiceProvider();
            _renderer = new HtmlRenderer(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public string Render()
        {
            var html = _renderer.Dispatcher.InvokeAsync(async () =>
            {
                var output = await _renderer.RenderComponentAsync<WorkspaceSwitchOverlay>(
                    ParameterView.Empty);
                return output.ToHtmlString();
            }).GetAwaiter().GetResult();

            return html;
        }

        public void Dispose()
        {
            _renderer.Dispose();
            _services.Dispose();
        }
    }
}
