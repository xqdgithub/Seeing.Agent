using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Components;

public class SessionWindowCommandDispatchContractTests
{
    [Fact]
    public void CommandResult_Should_Go_Through_Dispatcher()
    {
        var razor = ReadSessionWindow();
        razor.Should().Contain("CommandResultDispatcher");
        razor.Should().Contain("DispatchAsync");
    }

    [Fact]
    public void Window_Should_Register_Core_Actions_And_Rebind_On_Session_Change()
    {
        var razor = ReadSessionWindow();
        razor.Should().Contain("CommandActionNames.PageNavigate");
        razor.Should().Contain("CommandActionNames.ModelSet");
        razor.Should().Contain("CommandActionNames.AgentSet");
        razor.Should().Contain("_actionsSessionId");
    }

    [Fact]
    public void Only_Full_Window_Should_Register_Actions()
    {
        ReadSessionWindow()
            .Should().Contain("ViewMode != Services.SessionWindowMode.Full",
                "Summary 卡片不处理命令事件，也不应注册宿主动作（避免遮蔽同会话 Full 窗口的处理器）");
    }

    private static string ReadSessionWindow()
    {
        var path = Path.Combine(
            RepoRoot(),
            "samples",
            "Seeing.Agent.WebUI",
            "Components",
            "SessionWindow.razor");
        File.Exists(path).Should().BeTrue($"应找到源文件 {path}");
        return File.ReadAllText(path);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", ".."));
}
