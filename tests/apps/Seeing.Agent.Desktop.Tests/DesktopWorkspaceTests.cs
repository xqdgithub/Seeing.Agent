using FluentAssertions;
using Seeing.Agent.Desktop.Services;

namespace Seeing.Agent.Desktop.Tests;

/// <summary>
/// 桌面启动位置解析。
/// <para>
/// 这里守住两条不变量：
/// </para>
/// <list type="number">
/// <item><b>exe 所在目录永远不能成为起始位置</b>。从资源管理器/快捷方式双击时
/// cwd 就是 exe 目录（安装目录，Windows 下常在 <c>Program Files</c>）；
/// 若把它当工作区，Agent 的会话库、记忆与文件写入会落到不可写目录。</item>
/// <item><b>桌面壳不读用户级 GlobalWorkspaceRoot</b>。Core 的语义是
/// 「配置了全局目录也要 <c>Workspace.UseGlobal</c> 为真才生效」，
/// 壳若自己读就会绕过这个开关。</item>
/// </list>
/// </summary>
public class DesktopWorkspaceTests
{
    private const string ExeDir = @"C:\Program Files\Seeing";
    private const string LocalAppData = @"C:\Users\tester\AppData\Local";

    private static Func<string, bool> AllExist => _ => true;

    private static DesktopWorkspaceResolution Resolve(
        string currentDirectory = @"D:\work\project",
        string? env = null,
        Func<string, bool>? exists = null)
        => DesktopWorkspace.Resolve(
            currentDirectory,
            ExeDir,
            env,
            LocalAppData,
            exists ?? AllExist);

    [Fact]
    public void Resolve_Should_Use_Environment_When_Present()
    {
        var result = Resolve(env: @"E:\ai_data\seeing-wd", currentDirectory: @"D:\work\project");

        result.Path.Should().Be(@"E:\ai_data\seeing-wd");
        result.Source.Should().Be(DesktopWorkspaceSource.EnvironmentVariable);
    }

    [Fact]
    public void Resolve_Should_Prefer_Environment_Over_CurrentDirectory()
    {
        var result = Resolve(env: @"E:\env-wd", currentDirectory: @"D:\work\project");

        result.Source.Should().Be(DesktopWorkspaceSource.EnvironmentVariable);
        result.Path.Should().Be(@"E:\env-wd");
    }

    [Fact]
    public void Resolve_Should_Ignore_Environment_When_Directory_Missing()
    {
        var result = Resolve(
            env: @"E:\missing",
            currentDirectory: @"D:\work\project",
            exists: p => p != @"E:\missing");

        result.Source.Should().Be(DesktopWorkspaceSource.CurrentDirectory);
    }

    [Fact]
    public void Resolve_Should_Use_CurrentDirectory_When_Launched_From_Terminal()
    {
        // 从终端/IDE 启动：cwd 是用户有意所在的项目目录，应当保留。
        var result = Resolve(currentDirectory: @"D:\work\project");

        result.Path.Should().Be(@"D:\work\project");
        result.Source.Should().Be(DesktopWorkspaceSource.CurrentDirectory);
    }

    [Fact]
    public void Resolve_Should_Never_Use_Executable_Directory_As_Workspace()
    {
        // 双击启动的典型形态：cwd == exe 目录。这是本类最重要的一条断言。
        var result = Resolve(currentDirectory: ExeDir);

        result.Path.Should().NotBe(ExeDir);
        result.Path.Should().Be(Path.Combine(LocalAppData, DesktopWorkspace.DefaultFolderName));
        result.Source.Should().Be(DesktopWorkspaceSource.ApplicationDefault);
    }

    [Fact]
    public void Resolve_Should_Treat_Executable_Directory_Case_Insensitively_And_Ignore_Trailing_Separator()
    {
        // Windows 路径大小写与尾随分隔符都不应让它绕过判定。
        var result = Resolve(currentDirectory: @"c:\program files\seeing\");

        result.Path.Should().NotBe(ExeDir);
        result.Source.Should().Be(DesktopWorkspaceSource.ApplicationDefault);
    }

    [Fact]
    public void Resolve_Should_Return_LocalAppData_Seeing_As_Application_Default()
    {
        var result = Resolve(currentDirectory: ExeDir);

        // 产品名是 Seeing（不是 Seeing.Agent）；默认落在 LOCALAPPDATA 下，
        // 不污染文档目录，也避开 OneDrive 同步与只读安装目录。
        result.Path.Should().Be(@"C:\Users\tester\AppData\Local\Seeing");
        result.Path.Should().EndWith(DesktopWorkspace.DefaultFolderName);
    }

    [Fact]
    public void Resolve_Should_Fall_Back_To_Application_Default_When_Nothing_Valid()
    {
        // cwd 为空（获取失败）、exe 目录 —— 退化到默认目录。
        var result = Resolve(currentDirectory: string.Empty);

        result.Source.Should().Be(DesktopWorkspaceSource.ApplicationDefault);
        result.Path.Should().Be(Path.Combine(LocalAppData, DesktopWorkspace.DefaultFolderName));
    }

    [Fact]
    public void DesktopShell_Should_Not_Resolve_GlobalWorkspaceRoot_Itself()
    {
        // Core 的契约：GlobalWorkspaceRoot 仅在 Workspace.UseGlobal 为真时生效。
        // 桌面壳若自己读该配置，就会绕过开关，让「项目里选择不使用全局」失效。
        // 判据：DesktopWorkspace 不暴露任何读取全局工作区的入口，也不含该配置键的处理。
        var publicMembers = typeof(DesktopWorkspace)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)
            .ToList();

        publicMembers.Should().NotContain(
            name => name.Contains("GlobalWorkspace", StringComparison.OrdinalIgnoreCase),
            "桌面壳只决定进程起始位置，工作区由 Core 的 WorkspaceProvider 按 UseGlobal 裁决");
    }

    [Fact]
    public void Describe_Should_Produce_Chinese_Label_For_Log()
    {
        new DesktopWorkspaceResolution("x", DesktopWorkspaceSource.ApplicationDefault)
            .Describe().Should().Be("应用默认目录");
        new DesktopWorkspaceResolution("x", DesktopWorkspaceSource.CurrentDirectory)
            .Describe().Should().Be("启动目录");
        new DesktopWorkspaceResolution("x", DesktopWorkspaceSource.EnvironmentVariable)
            .Describe().Should().Be("环境变量");
    }
}
