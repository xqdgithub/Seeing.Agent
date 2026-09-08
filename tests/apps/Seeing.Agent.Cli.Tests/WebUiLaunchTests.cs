using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class WebUiLaunchTests
{
    [Fact]
    public void BuildArguments_AndEnvironment_ShouldUseTheSamePort()
    {
        const int port = 25123;

        var startInfo = ServiceProcessManager.CreateStartInfo(
            "Seeing.Agent.WebUI.dll",
            Environment.CurrentDirectory,
            WebUiLaunch.BuildArguments(port),
            WebUiLaunch.BuildEnvironment(port));

        startInfo.ArgumentList.Should().ContainInOrder(
            "Seeing.Agent.WebUI.dll",
            "--urls",
            "http://127.0.0.1:25123");
        startInfo.Environment["ASPNETCORE_URLS"].Should().Be("http://127.0.0.1:25123");
        startInfo.Environment["DOTNET_URLS"].Should().Be("http://127.0.0.1:25123");
        WebUiLaunch.BuildUrl(port).Should().Be("http://127.0.0.1:25123");
    }
}
