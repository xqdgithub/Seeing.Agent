using FluentAssertions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Commands;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

/// <summary>
/// CommandResult 扩展方法测试：With* 系列必须保留全部字段（此前 WithData 重建会丢失
/// ShouldContinue / RemoveCommandMessage 等，导致 /new、/fork 短路标志丢失）。
/// </summary>
public class CommandResultExtensionsTests
{
    [Fact]
    public void WithNavigation_ShouldPreserveShouldContinueAndRemoveCommandMessage()
    {
        var result = CommandResult.Ok("go", shouldContinue: false)
            .WithCommandMessageRetained()
            .WithNavigation("/session/abc");

        result.ShouldContinue.Should().BeFalse("WithNavigation 不得丢失短路标志");
        result.RemoveCommandMessage.Should().BeFalse("WithNavigation 不得丢失命令消息保留声明");
        result.GetNavigationTarget().Should().Be("/session/abc");
    }

    [Fact]
    public void WithCommandMessageRetained_ShouldKeepOtherFields()
    {
        var result = CommandResult.Ok("msg", shouldContinue: false).WithCommandMessageRetained();

        result.RemoveCommandMessage.Should().BeFalse();
        result.ShouldContinue.Should().BeFalse();
        result.Success.Should().BeTrue();
        result.Message.Should().Be("msg");
    }
}
