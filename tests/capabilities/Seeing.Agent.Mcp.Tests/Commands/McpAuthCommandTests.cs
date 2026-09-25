using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Core.Commands;
using Seeing.Agent.Mcp.Commands;
using Seeing.Agent.Mcp.OAuth;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.Commands;

/// <summary>
/// /mcp-auth 命令行为，以及 McpModule 在 Activate/Deactivate 时对称注册/注销命令。
/// </summary>
public class McpAuthCommandTests
{
    [Fact]
    public void Metadata_ShouldDescribeMcpAuthCommand()
    {
        var command = new McpAuthCommand(Mock.Of<IMcpOAuthAuthorizer>());

        command.Metadata.Name.Should().Be("mcp-auth");
        command.Metadata.Category.Should().Be(CommandCategory.Tools);
        command.Metadata.Usage.Should().Contain("<server>");
    }

    [Fact]
    public async Task ExecuteAsync_WithoutServer_ShouldFailWithoutInvokingAuthorizer()
    {
        var authorizer = new Mock<IMcpOAuthAuthorizer>();
        var command = new McpAuthCommand(authorizer.Object);

        var result = await command.ExecuteAsync(new CommandContext { Arguments = "  " });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("服务器");
        authorizer.Verify(a => a.AuthorizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithServer_ShouldInvokeAuthorizerAndReportSuccess()
    {
        var authorizer = new Mock<IMcpOAuthAuthorizer>();
        authorizer.Setup(a => a.AuthorizeAsync("github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthResult(true, McpAuthStatus.Authenticated));
        var command = new McpAuthCommand(authorizer.Object);

        var result = await command.ExecuteAsync(new CommandContext { Arguments = "github" });

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("github");
        authorizer.Verify(a => a.AuthorizeAsync("github", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAuthorizerFails_ShouldReturnFailureWithError()
    {
        var authorizer = new Mock<IMcpOAuthAuthorizer>();
        authorizer.Setup(a => a.AuthorizeAsync("github", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthResult(false, McpAuthStatus.NeedsAuthorization, "无法自动打开浏览器"));
        var command = new McpAuthCommand(authorizer.Object);

        var result = await command.ExecuteAsync(new CommandContext { Arguments = "github" });

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("无法自动打开浏览器");
    }

    [Fact]
    public async Task Module_Activate_RegistersMcpAuthCommand_Deactivate_Unregisters()
    {
        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.InitializeAsync(
                It.IsAny<IReadOnlyDictionary<string, McpServerConfig>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        manager.Setup(m => m.ShutdownAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        manager.Setup(m => m.UnregisterAllToolsAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        manager.Setup(m => m.GetTools()).Returns(Array.Empty<McpToolInfo>());

        var registry = new CommandRegistry();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICommandRegistry>(registry);
        services.AddSingleton(manager.Object);
        services.AddSingleton(Mock.Of<IMcpOAuthAuthorizer>());
        await using var sp = services.BuildServiceProvider();

        var module = new McpModule();
        await module.ActivateAsync(sp, TestContext.Current.CancellationToken);

        registry.HasCommand("mcp-auth").Should().BeTrue();
        registry.GetCommand("mcp-auth")!.Metadata.Source.Should().Be("mcp");

        await module.DeactivateAsync(sp, TestContext.Current.CancellationToken);

        registry.HasCommand("mcp-auth").Should().BeFalse();
    }
}
