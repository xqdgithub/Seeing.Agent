using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Razor.Hosting;
using Seeing.Agent.WebUI.Hosting;
using System.Reflection;

namespace Seeing.Agent.WebUI.Tests.Hosting;

/// <summary>
/// 守卫 WebUI 宿主组装的可复用性。
/// <para>
/// 浏览器宿主（WebUI 自身的 Program）与桌面宿主（Seeing.Agent.Desktop）共用
/// <see cref="WebUiHost"/> 的组装入口；一旦组装逻辑被收回顶层语句，
/// 或 BuildAsync 变成「组装即运行」，桌面外壳就无法在自己的进程里
/// 绑定动态端口后再开窗，两个宿主也会开始漂移。
/// </para>
/// </summary>
public class WebUiHostTests
{
    [Fact]
    public void BuildAsync_Should_Be_Awaitable_And_Return_Application()
    {
        // 按「名称 + 首参类型」定位，而不是按完整签名。
        // 尾部的可选参数（contentRoot / configureLogging）会随宿主能力增长，
        // 用精确签名查找会让每加一个可选参数就误报「方法不存在」——
        // 那是签名演进，不是契约被破坏。
        var method = typeof(WebUiHost)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m =>
                m.Name == nameof(WebUiHost.BuildAsync)
                && m.GetParameters() is [var first, ..]
                && first.ParameterType == typeof(string[]));

        method.Should().NotBeNull("桌面宿主依赖 WebUiHost.BuildAsync 作为唯一组装入口");

        // 可 await 且返回 WebApplication —— 调用方需要先拿到 app 再绑定端口/开窗。
        method!.ReturnType.Should().Be<Task<WebApplication>>();

        var parameters = method.GetParameters();

        // 首参是命令行参数，必须必填（避免调用方漏传 args 却静默用空数组）。
        parameters[0].HasDefaultValue.Should().BeFalse();

        parameters.Should().HaveCountGreaterThanOrEqualTo(2,
            "内容根是桌面宿主必需的扩展点");

        // 内容根必须可选：浏览器宿主只调 BuildAsync(args)。
        parameters[1].Name.Should().Be("contentRoot");
        parameters[1].HasDefaultValue.Should().BeTrue(
            "浏览器宿主只传 args，内容根只能是可选参数");

        // 其余扩展点（如 configureLogging）同样必须可选，
        // 否则既有的两参调用会在编译期被破坏。
        parameters.Skip(2).Should().OnlyContain(
            p => p.HasDefaultValue,
            "新增的尾部参数必须带默认值，保证既有调用点不被破坏");
    }

    [Fact]
    public void WebUiHost_Should_Live_In_WebUi_Assembly_Side_By_Side_With_Pages()
    {
        // 宿主组装与 Razor 页面必须同程序集：桌面宿主以「外部入口程序集」身份启动时，
        // 需要把该程序集注册为 MVC 应用部件（见 WebUiHost 内的 AddApplicationPart），
        // 否则 _Host.cshtml 等页面不会被程序集发现，表现为整窗口 404。
        var assembly = typeof(WebUiHost).Assembly;

        assembly.GetName().Name.Should().Be("Seeing.Agent.WebUI");

        // Razor 编译产物带 RazorCompiledItemAttribute；_Host 页就在其中。
        var compiledItems = assembly
            .GetCustomAttributes<RazorCompiledItemAttribute>()
            .Select(a => a.Identifier)
            .ToList();

        compiledItems.Should().NotBeEmpty();
        compiledItems.Should().Contain(
            identifier => identifier.StartsWith("/Pages/_Host", StringComparison.OrdinalIgnoreCase),
            "桌面宿主依赖 WebUI 程序集内的 _Host 页面作为 Blazor Server 的承载页");
    }
}
