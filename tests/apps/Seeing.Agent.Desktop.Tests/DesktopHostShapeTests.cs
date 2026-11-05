using System.Xml.Linq;
using FluentAssertions;

namespace Seeing.Agent.Desktop.Tests;

/// <summary>
/// 桌面宿主的打包/宿主形态守卫。
/// <para>
/// 这些断言针对 csproj 的元数据而非运行时行为，因为对应的失败模式
/// 恰恰是「能跑但体验不对」（例如多一个控制台窗口），没有自动化测试就会复发。
/// </para>
/// </summary>
public class DesktopHostShapeTests
{
    private static XDocument LoadCsproj()
    {
        // 测试输出目录 → 仓库根：tests/apps/<name>/bin/<cfg>/<tfm>/ 上溯 7 层
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "Seeing.Agent.Desktop", "Seeing.Agent.Desktop.csproj");
            if (File.Exists(candidate))
                return XDocument.Load(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 Seeing.Agent.Desktop.csproj");
    }

    private static string? Property(XDocument doc, string name)
        => doc.Descendants(name).FirstOrDefault()?.Value.Trim();

    [Fact]
    public void OutputType_Should_Be_WinExe_To_Avoid_A_Console_Window()
    {
        // OutputType=Exe 生成「控制台子系统」程序：双击时额外弹出一个 cmd 窗口
        // 承载 stdout/stderr，用户会同时看到黑框 + 应用窗口两个窗口。
        // WinExe 挂到 GUI 子系统，不分配控制台。
        Property(LoadCsproj(), "OutputType").Should().Be(
            "WinExe",
            "桌面应用不能额外弹出控制台窗口；日志改由 DesktopFileLoggerProvider 落盘");
    }

    [Fact]
    public void SelfContained_Should_Be_False_To_Avoid_NETSDK1150()
    {
        // WebUI 是 OutputType=Exe；SDK 规则禁止自包含的可执行文件引用非自包含的可执行文件。
        // VS 的发布配置不会像命令行 -p:SelfContained=true 那样广播该属性，
        // 因此在项目里显式固定，保证两条发布路径一致。
        Property(LoadCsproj(), "SelfContained").Should().Be(
            "false",
            "自包含会让 VS 发布路径触发 NETSDK1150");
    }

    [Fact]
    public void PublishProfile_Should_Not_Force_SelfContained()
    {
        // 发布配置里一旦出现 SelfContained=true，就等于回到 NETSDK1150。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? profile = null;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "samples", "Seeing.Agent.Desktop", "Properties", "PublishProfiles", "FolderProfile.pubxml");
            if (File.Exists(candidate))
            {
                profile = candidate;
                break;
            }
            dir = dir.Parent;
        }

        profile.Should().NotBeNull();

        var doc = XDocument.Load(profile!);
        var selfContained = doc.Descendants("SelfContained").FirstOrDefault()?.Value.Trim();

        selfContained.Should().NotBe(
            "true",
            "发布配置里的 SelfContained=true 不会广播给项目引用，必然触发 NETSDK1150");
    }

    [Fact]
    public void LaunchSettings_Should_Not_Launch_Browser()
    {
        // 桌面应用自带原生窗口；launchSettings 里 launchBrowser=true 会在 F5 时
        // 额外打开一个浏览器标签页，形成「两个界面」。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? settings = null;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(
                dir.FullName, "samples", "Seeing.Agent.Desktop", "Properties", "launchSettings.json");
            if (File.Exists(candidate))
            {
                settings = candidate;
                break;
            }
            dir = dir.Parent;
        }

        settings.Should().NotBeNull();

        var text = File.ReadAllText(settings!);
        text.Should().Contain("\"launchBrowser\": false");
    }

    [Fact]
    public void PublishScript_Should_Exist_And_Clean_Before_Publish()
    {
        // obj/<Config>/net10.0/ 下并存多个 RID 分支（win-x64/linux-x64/ref/refint），
        // 共用同一组 .cache 文件；交替用不同 -r 构建会让 MSBuild 增量判断读到
        // 交叉污染的时间戳，出现「改了 WebUI 公共方法签名，宿主仍按旧签名编译」
        // → CS1501。脚本的清理步骤就是为此存在，不能被删掉。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? script = null;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(
                dir.FullName, "samples", "Seeing.Agent.Desktop", "tools", "publish.ps1");
            if (File.Exists(candidate))
            {
                script = candidate;
                break;
            }
            dir = dir.Parent;
        }

        script.Should().NotBeNull("桌面发布必须走带清理的脚本");

        var text = File.ReadAllText(script!);
        text.Should().Contain("NoClean", "脚本要提供跳过清理的开关");
        text.Should().Contain("Remove-Item -Recurse -Force $full",
            "脚本必须清理受影响项目的 obj/bin，否则 RID 交叉污染会让构建假成功");
    }
}
