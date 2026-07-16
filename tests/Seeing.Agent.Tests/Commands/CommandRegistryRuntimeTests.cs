using Seeing.Agent.Commands;
using Seeing.Agent.Core.Models;
using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tests.Commands;

/// <summary>
/// CommandRegistry Runtime 隔离功能测试
/// </summary>
public class CommandRegistryRuntimeTests
{
    /// <summary>
    /// 测试命令 - 支持 Native Runtime
    /// </summary>
    private class NativeCommand : ICommand
    {
        public CommandMetadata Metadata => new()
        {
            Name = "test-native",
            Description = "Native test command",
            SupportedRuntimes = new[] { AgentRuntime.Native }
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok("Native executed"));
    }

    /// <summary>
    /// 测试命令 - 支持 ACP Passthrough Runtime
    /// </summary>
    private class AcpCommand : ICommand
    {
        public CommandMetadata Metadata => new()
        {
            Name = "test-acp",
            Description = "ACP test command",
            SupportedRuntimes = new[] { AgentRuntime.AcpPassthrough }
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok("ACP executed"));
    }

    /// <summary>
    /// 测试命令 - 支持所有 Runtime（默认）
    /// </summary>
    private class UniversalCommand : ICommand
    {
        public CommandMetadata Metadata => new()
        {
            Name = "test-universal",
            Description = "Universal test command",
            SupportedRuntimes = Array.Empty<AgentRuntime>() // 空数组 = 支持所有
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok("Universal executed"));
    }

    /// <summary>
    /// 测试命令 - 同名但不同 Runtime
    /// </summary>
    private class ClearNativeCommand : ICommand
    {
        public CommandMetadata Metadata => new()
        {
            Name = "clear",
            Description = "Clear (Native)",
            SupportedRuntimes = new[] { AgentRuntime.Native }
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok("Clear native"));
    }

    private class ClearAcpCommand : ICommand
    {
        public CommandMetadata Metadata => new()
        {
            Name = "clear",
            Description = "Clear (ACP)",
            SupportedRuntimes = new[] { AgentRuntime.AcpPassthrough }
        };

        public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(CommandResult.Ok("Clear ACP"));
    }

    [Fact]
    public void Register_NativeCommand_ShouldBeFoundForNativeRuntime()
    {
        // Arrange
        var registry = new CommandRegistry();
        var command = new NativeCommand();

        // Act
        registry.Register(command);
        var found = registry.GetCommand("test-native", AgentRuntime.Native);

        // Assert
        found.Should().NotBeNull();
        found.Should().Be(command);
    }

    [Fact]
    public void Register_NativeCommand_ShouldNotBeFoundForAcpRuntime()
    {
        // Arrange
        var registry = new CommandRegistry();
        var command = new NativeCommand();

        // Act
        registry.Register(command);
        var found = registry.GetCommand("test-native", AgentRuntime.AcpPassthrough);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void Register_AcpCommand_ShouldBeFoundForAcpRuntime()
    {
        // Arrange
        var registry = new CommandRegistry();
        var command = new AcpCommand();

        // Act
        registry.Register(command);
        var found = registry.GetCommand("test-acp", AgentRuntime.AcpPassthrough);

        // Assert
        found.Should().NotBeNull();
        found.Should().Be(command);
    }

    [Fact]
    public void Register_AcpCommand_ShouldNotBeFoundForNativeRuntime()
    {
        // Arrange
        var registry = new CommandRegistry();
        var command = new AcpCommand();

        // Act
        registry.Register(command);
        var found = registry.GetCommand("test-acp", AgentRuntime.Native);

        // Assert
        found.Should().BeNull();
    }

    [Fact]
    public void Register_UniversalCommand_ShouldBeFoundForAllRuntimes()
    {
        // Arrange
        var registry = new CommandRegistry();
        var command = new UniversalCommand();

        // Act
        registry.Register(command);
        var foundNative = registry.GetCommand("test-universal", AgentRuntime.Native);
        var foundAcp = registry.GetCommand("test-universal", AgentRuntime.AcpPassthrough);

        // Assert
        foundNative.Should().NotBeNull();
        foundAcp.Should().NotBeNull();
        foundNative.Should().Be(command);
        foundAcp.Should().Be(command);
    }

    [Fact]
    public void Register_SameNameDifferentRuntimes_ShouldRouteCorrectly()
    {
        // Arrange
        var registry = new CommandRegistry();
        var nativeClear = new ClearNativeCommand();
        var acpClear = new ClearAcpCommand();

        // Act
        registry.Register(nativeClear);
        registry.Register(acpClear);

        var foundNative = registry.GetCommand("clear", AgentRuntime.Native);
        var foundAcp = registry.GetCommand("clear", AgentRuntime.AcpPassthrough);

        // Assert
        foundNative.Should().NotBeNull();
        foundAcp.Should().NotBeNull();
        foundNative.Should().Be(nativeClear);
        foundAcp.Should().Be(acpClear);
    }

    [Fact]
    public void GetCommandsByRuntime_Native_ShouldReturnOnlyNativeAndUniversalCommands()
    {
        // Arrange
        var registry = new CommandRegistry();
        registry.Register(new NativeCommand());
        registry.Register(new AcpCommand());
        registry.Register(new UniversalCommand());

        // Act
        var nativeCommands = registry.GetCommandsByRuntime(AgentRuntime.Native).ToList();

        // Assert
        nativeCommands.Should().HaveCount(2);
        nativeCommands.Select(c => c.Metadata.Name).Should().Contain("test-native");
        nativeCommands.Select(c => c.Metadata.Name).Should().Contain("test-universal");
        nativeCommands.Select(c => c.Metadata.Name).Should().NotContain("test-acp");
    }

    [Fact]
    public void GetCommandsByRuntime_Acp_ShouldReturnOnlyAcpAndUniversalCommands()
    {
        // Arrange
        var registry = new CommandRegistry();
        registry.Register(new NativeCommand());
        registry.Register(new AcpCommand());
        registry.Register(new UniversalCommand());

        // Act
        var acpCommands = registry.GetCommandsByRuntime(AgentRuntime.AcpPassthrough).ToList();

        // Assert
        acpCommands.Should().HaveCount(2);
        acpCommands.Select(c => c.Metadata.Name).Should().Contain("test-acp");
        acpCommands.Select(c => c.Metadata.Name).Should().Contain("test-universal");
        acpCommands.Select(c => c.Metadata.Name).Should().NotContain("test-native");
    }

    [Fact]
    public void SupportsRuntime_EmptyArray_ShouldReturnTrueForAllRuntimes()
    {
        // Arrange
        var metadata = new CommandMetadata
        {
            Name = "test",
            Description = "test",
            SupportedRuntimes = Array.Empty<AgentRuntime>()
        };

        // Act & Assert
        metadata.SupportsRuntime(AgentRuntime.Native).Should().BeTrue();
        metadata.SupportsRuntime(AgentRuntime.AcpPassthrough).Should().BeTrue();
    }

    [Fact]
    public void SupportsRuntime_SpecificRuntime_ShouldReturnTrueOnlyForThatRuntime()
    {
        // Arrange
        var metadata = new CommandMetadata
        {
            Name = "test",
            Description = "test",
            SupportedRuntimes = new[] { AgentRuntime.Native }
        };

        // Act & Assert
        metadata.SupportsRuntime(AgentRuntime.Native).Should().BeTrue();
        metadata.SupportsRuntime(AgentRuntime.AcpPassthrough).Should().BeFalse();
    }

    [Fact]
    public void CommandResult_ShouldContinue_ShouldDefaultToTrue()
    {
        // Act
        var result = CommandResult.Ok("test");

        // Assert
        result.ShouldContinue.Should().BeTrue();
    }

    [Fact]
    public void CommandResult_ShouldContinue_CanBeSetToFalse()
    {
        // Act
        var result = CommandResult.Ok("test", shouldContinue: false);

        // Assert
        result.ShouldContinue.Should().BeFalse();
    }
}
