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
/// <see cref="WebUiHost.BuildAsync(string[], string?)"/>；一旦组装逻辑被收回
/// 顶层语句，或 BuildAsync 变成「组装即运行」，桌面外壳就无法在自己的进程里
/// 绑定动态端口后再开窗，两个宿主也会开始漂移。
/// </para>
/// </summary>
public class WebUiHostTests
{
    [Fact]
    public void BuildAsync_Should_Be_Awaitable_And_Return_Application()
    {
        // 方法存在、可 await、返回 WebApplication —— 桌面宿主依赖这个签名。
        var method = typeof(WebUiHost).GetMethod(
            nameof(WebUiHost.BuildAsync),
            [typeof(string[]), typeof(string)]);

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be<Task<WebApplication>>();

        // 可选的内容根参数：桌面宿主必须能把内容根指向自身输出目录，
        // 否则 WebApplication.CreateBuilder 会以宿主进程的工作目录为准。
        method.GetParameters()[1].HasDefaultValue.Should().BeTrue();
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
