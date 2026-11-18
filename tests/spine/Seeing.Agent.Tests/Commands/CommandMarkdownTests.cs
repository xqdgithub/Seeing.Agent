using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Core.Commands.Discovery;
using Seeing.Session.Core;
using System.Reflection;
using Xunit;

namespace Seeing.Agent.Tests.Commands
{
    public class CommandMetadataTests
    {
        [Fact]
        public void Template_Property_ShouldInitializeCorrectly()
        {
            // Arrange & Act
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test command",
                Template = "Hello {{1}}"
            };

            // Assert
            metadata.Template.Should().Be("Hello {{1}}");
        }

        [Fact]
        public void Template_Property_ShouldDefaultToNull()
        {
            // Arrange & Act
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test command"
            };

            // Assert
            metadata.Template.Should().BeNull();
        }

        [Fact]
        public void Agent_Property_ShouldInitializeCorrectly()
        {
            // Arrange & Act
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test command",
                Agent = "coder"
            };

            // Assert
            metadata.Agent.Should().Be("coder");
        }

        [Fact]
        public void Agent_Property_ShouldDefaultToNull()
        {
            // Arrange & Act
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test command"
            };

            // Assert
            metadata.Agent.Should().BeNull();
        }
    }

    public class CommandContextTests
    {
        [Fact]
        public void TargetAgent_Field_ShouldInitializeCorrectly()
        {
            // Arrange & Act
            var context = new CommandContext
            {
                CommandName = "test",
                TargetAgent = "reviewer"
            };

            // Assert
            context.TargetAgent.Should().Be("reviewer");
        }

        [Fact]
        public void TargetAgent_Field_ShouldDefaultToNull()
        {
            // Arrange & Act
            var context = new CommandContext();

            // Assert
            context.TargetAgent.Should().BeNull();
        }
    }

    public class CommandDiscoveryMarkdownTests
    {
        private readonly string _testCommandsDir;

        public CommandDiscoveryMarkdownTests()
        {
            _testCommandsDir = Path.Combine(Path.GetTempPath(), "SeeingAgent_Commands_Tests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testCommandsDir);
        }

        [Fact]
        public async Task DiscoverFromMarkdownAsync_ShouldDiscoverValidCommands()
        {
            // Arrange
            var commandFile = Path.Combine(_testCommandsDir, "test.md");
            var content = @"---
name: test-cmd
description: Test command
aliases:
  - t
category: tools
agent: coder
---
Hello {{1}}, this is {{arguments}}";
            await File.WriteAllTextAsync(commandFile, content, TestContext.Current.CancellationToken);

            var discovery = new CommandDiscovery();

            // Act
            var commands = await discovery.DiscoverFromMarkdownAsync(_testCommandsDir, TestContext.Current.CancellationToken);

            // Assert
            commands.Should().HaveCount(1);
            var cmd = commands.First();
            cmd.Metadata.Name.Should().Be("test-cmd");
            cmd.Metadata.Description.Should().Be("Test command");
            cmd.Metadata.Agent.Should().Be("coder");
            cmd.Metadata.Category.Should().Be(CommandCategory.Tools);
            cmd.Metadata.Template.Should().Contain("Hello {{1}}");
        }

        [Fact]
        public async Task DiscoverFromMarkdownAsync_ShouldSkipInvalidFiles()
        {
            // Arrange - File without name
            var invalidFile = Path.Combine(_testCommandsDir, "invalid.md");
            await File.WriteAllTextAsync(invalidFile, "---\ndescription: No name\n---\nContent", TestContext.Current.CancellationToken);

            // Arrange - File without frontmatter
            var noFrontmatterFile = Path.Combine(_testCommandsDir, "no-frontmatter.md");
            await File.WriteAllTextAsync(noFrontmatterFile, "Just content", TestContext.Current.CancellationToken);

            var discovery = new CommandDiscovery();

            // Act
            var commands = await discovery.DiscoverFromMarkdownAsync(_testCommandsDir, TestContext.Current.CancellationToken);

            // Assert
            commands.Should().BeEmpty();
        }

        [Fact]
        public async Task DiscoverFromMarkdownAsync_ShouldHandleNonExistentDirectory()
        {
            // Arrange
            var discovery = new CommandDiscovery();

            // Act
            var commands = await discovery.DiscoverFromMarkdownAsync("/non/existent/path", TestContext.Current.CancellationToken);

            // Assert
            commands.Should().BeEmpty();
        }
    }

    public class TemplateRenderingTests
    {
        [Fact]
        public async Task RenderTemplate_ShouldReplacePositionalArguments()
        {
            // Arrange
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test",
                Template = "Hello {{1}}, your message is: {{2}}"
            };

            // Use reflection to test the internal MarkdownCommand
            var discoveryType = typeof(CommandDiscovery);
            var markdownCommandType = discoveryType.GetNestedType("MarkdownCommand", BindingFlags.NonPublic | BindingFlags.Instance);

            var command = (ICommand?)Activator.CreateInstance(markdownCommandType!, metadata, null);

            var context = new CommandContext
            {
                Arguments = "Alice Hello World"
            };

            // Act
            var result = await command!.ExecuteAsync(context, TestContext.Current.CancellationToken);

            // Assert
            result.Success.Should().BeTrue();
            result.Message.Should().Be("Hello Alice, your message is: Hello World");
        }

        [Fact]
        public async Task RenderTemplate_ShouldReplaceArgumentsPlaceholder()
        {
            // Arrange
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test",
                Template = "Full arguments: {{arguments}}"
            };

            var discoveryType = typeof(CommandDiscovery);
            var markdownCommandType = discoveryType.GetNestedType("MarkdownCommand", BindingFlags.NonPublic | BindingFlags.Instance);
            var command = (ICommand?)Activator.CreateInstance(markdownCommandType!, metadata, null);

            var context = new CommandContext
            {
                Arguments = "hello world test"
            };

            // Act
            var result = await command!.ExecuteAsync(context, TestContext.Current.CancellationToken);

            // Assert
            result.Success.Should().BeTrue();
            result.Message.Should().Be("Full arguments: hello world test");
        }

        [Fact]
        public async Task RenderTemplate_ShouldUseEmptyStringWhenArgumentMissing()
        {
            // Arrange
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test",
                Template = "First: {{1}}, Second: {{2}}"
            };

            var discoveryType = typeof(CommandDiscovery);
            var markdownCommandType = discoveryType.GetNestedType("MarkdownCommand", BindingFlags.NonPublic | BindingFlags.Instance);
            var command = (ICommand?)Activator.CreateInstance(markdownCommandType!, metadata, null);

            var context = new CommandContext
            {
                Arguments = "only-one"
            };

            // Act
            var result = await command!.ExecuteAsync(context, TestContext.Current.CancellationToken);

            // Assert
            result.Success.Should().BeTrue();
            result.Message.Should().Be("First: only-one, Second: ");
        }
    }

    public class MarkdownCommandExecutionTests
    {
        /// <summary>
        /// 模板命令 = 「把命令原文那条 user 消息改写成展开结果，再继续执行 Agent」。
        /// 旧实现只移除命令消息、不写回展开结果，模型这一轮就收不到用户输入（模板等于没生效）。
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Rewrite_CommandMessage_With_Rendered_Text()
        {
            var session = new SessionData { Id = "s1" };
            session.AddMessage(SessionMessage.UserMessage("/review foo bar"));

            var sessions = new Mock<ISessionManager>();
            sessions.Setup(m => m.Get("s1")).Returns(session);
            sessions.Setup(m => m.UpdateSessionAsync(
                    "s1",
                    It.IsAny<Action<SessionData>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<string, Action<SessionData>, CancellationToken>((_, mutate, _) => mutate(session))
                .ReturnsAsync(session);

            var command = CreateCommand(new CommandMetadata
            {
                Name = "review",
                Description = "评审",
                Template = "请评审：{{arguments}}"
            });

            var result = await command.ExecuteAsync(
                new CommandContext
                {
                    SessionId = "s1",
                    Input = "/review foo bar",
                    Arguments = "foo bar",
                    Services = BuildServices(sessions.Object)
                },
                TestContext.Current.CancellationToken);

            result.Success.Should().BeTrue();
            result.ShouldContinue.Should().BeTrue();
            result.RemoveCommandMessage.Should().BeFalse("命令消息已被改写为展开结果，不能移除");
            result.NeedsRefresh.Should().BeTrue("界面需要重绘被改写的消息");
            result.Message.Should().Be("请评审：foo bar");

            session.Messages.Should().ContainSingle()
                .Which.Content.Should().Be("请评审：foo bar", "展开结果必须成为这一轮的用户输入");
        }

        [Fact]
        public async Task ExecuteAsync_Without_Session_Should_Keep_Command_Message()
        {
            var command = CreateCommand(new CommandMetadata
            {
                Name = "review",
                Description = "评审",
                Template = "请评审：{{arguments}}"
            });

            var result = await command.ExecuteAsync(
                new CommandContext { Input = "/review foo", Arguments = "foo" },
                TestContext.Current.CancellationToken);

            result.Success.Should().BeTrue();
            result.ShouldContinue.Should().BeTrue();
            result.RemoveCommandMessage.Should().BeFalse("无法改写时保留原文，避免静默丢掉用户输入");
            result.NeedsRefresh.Should().BeFalse();
        }

        private static ICommand CreateCommand(CommandMetadata metadata)
        {
            var markdownCommandType = typeof(CommandDiscovery)
                .GetNestedType("MarkdownCommand", BindingFlags.NonPublic | BindingFlags.Instance);
            return (ICommand)Activator.CreateInstance(markdownCommandType!, metadata, null)!;
        }

        private static IServiceProvider BuildServices(ISessionManager sessions)
        {
            var services = new ServiceCollection();
            services.AddSingleton(sessions);
            return services.BuildServiceProvider();
        }

        [Fact]
        public async Task ExecuteAsync_ShouldReturnSuccess_WhenTemplateIsValid()
        {
            // Arrange
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test",
                Template = "Result: {{arguments}}"
            };

            var discoveryType = typeof(CommandDiscovery);
            var markdownCommandType = discoveryType.GetNestedType("MarkdownCommand", BindingFlags.NonPublic | BindingFlags.Instance);
            var command = (ICommand?)Activator.CreateInstance(markdownCommandType!, metadata, null);

            var context = new CommandContext
            {
                Arguments = "success"
            };

            // Act
            var result = await command!.ExecuteAsync(context, TestContext.Current.CancellationToken);

            // Assert
            result.Success.Should().BeTrue();
            result.Message.Should().Be("Result: success");
        }

        [Fact]
        public async Task ExecuteAsync_ShouldReturnFail_WhenTemplateIsEmpty()
        {
            // Arrange
            var metadata = new CommandMetadata
            {
                Name = "test",
                Description = "Test",
                Template = null
            };

            var discoveryType = typeof(CommandDiscovery);
            var markdownCommandType = discoveryType.GetNestedType("MarkdownCommand", BindingFlags.NonPublic | BindingFlags.Instance);
            var command = (ICommand?)Activator.CreateInstance(markdownCommandType!, metadata, null);

            var context = new CommandContext();

            // Act
            var result = await command!.ExecuteAsync(context, TestContext.Current.CancellationToken);

            // Assert
            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Be("命令模板为空");
        }
    }
}
