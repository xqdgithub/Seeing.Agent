using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Hosting;

/// <summary>
/// 宿主组装入口的签名契约（供桌面外壳与浏览器宿主共用）。
/// <para>
/// 为什么要有这条编译期调用：<see cref="Seeing.Agent.WebUI.Hosting.WebUiHost.BuildAsync"/>
/// 以「可选尾参」的方式演进（contentRoot / configureLogging / startupDirectory）。
/// 桌面发布时会按 RID 构建 WebUI，RID 专属的引用程序集
/// （<c>obj/Release/&lt;tfm&gt;/&lt;rid&gt;/ref/WebUI.dll</c>）与 RID 无关的那份是两套产物：
/// 只增量构建过 RID 无关版本时，RID 那份可能停留在旧的签名上，
/// 表现为发布时报 <c>CS1501 “BuildAsync”方法没有采用 4 个参数的重载</c>。
/// </para>
/// <para>
/// 这里用一次真实的四参调用把它钉住：签名一旦不兼容，本测试项目编译就会失败，
/// 而不是等到发布桌面产物时才暴露。
/// </para>
/// </summary>
public class WebUiHostBuildSignatureTests
{
    [Fact]
    public void BuildAsync_Should_Accept_Four_Arguments_With_StartupDirectory()
    {
        // 不真正启动宿主：只取方法组并断言可赋值给四参委托，
        // 从而在编译期校验签名（含第 4 个可选参数）。
        Func<string[], string?, Action<Microsoft.Extensions.Logging.ILoggingBuilder>?, string?, Task<Microsoft.AspNetCore.Builder.WebApplication>> call
            = Seeing.Agent.WebUI.Hosting.WebUiHost.BuildAsync;

        call.Should().NotBeNull("桌面宿主需要传 4 个参数（含原始启动目录）");
    }
}
