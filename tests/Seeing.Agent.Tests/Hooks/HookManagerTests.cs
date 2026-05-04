using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Hooks;
using Xunit;

namespace Seeing.Agent.Tests.Hooks
{
    public class HookManagerTests
    {
        private readonly Mock<ILogger<HookManager>> _loggerMock;
        private readonly HookManager _manager;

        public HookManagerTests()
        {
            _loggerMock = new Mock<ILogger<HookManager>>();
            _manager = new HookManager(_loggerMock.Object);
        }

        [Fact]
        public void Register_ShouldAddHandler()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler.Setup(h => h.Priority).Returns(10);

            _manager.Register(handler.Object);

            _manager.Count(HookRegistry.ToolExecuteBefore).Should().Be(1);
        }

        [Fact]
        public void Register_ShouldOrderByPriority()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.Spec).Returns(HookRegistry.ChatBeforeStart);
            handler1.Setup(h => h.Priority).Returns(20);
            
            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.Spec).Returns(HookRegistry.ChatBeforeStart);
            handler2.Setup(h => h.Priority).Returns(10);

            _manager.Register(handler1.Object);
            _manager.Register(handler2.Object);

            _manager.Count(HookRegistry.ChatBeforeStart).Should().Be(2);
        }

        [Fact]
        public async Task TriggerAsync_ShouldCallAllHandlers()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler1.Setup(h => h.Priority).Returns(10);
            handler1.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
                .ReturnsAsync(HookResult.Success);
            
            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler2.Setup(h => h.Priority).Returns(20);
            handler2.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
                .ReturnsAsync(HookResult.Success);

            _manager.Register(handler1.Object);
            _manager.Register(handler2.Object);

            var payload = HookPayload.Blocking(HookRegistry.ToolExecuteBefore, "test-session");
            var result = await _manager.TriggerAsync(payload);

            result.Continue.Should().BeTrue();
            handler1.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Once);
            handler2.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Once);
        }

        [Fact]
        public async Task TriggerAsync_ShouldStopWhenContinueIsFalse()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler1.Setup(h => h.Priority).Returns(10);
            handler1.Setup(h => h.ExecuteAsync(It.IsAny<HookPayload>()))
                .ReturnsAsync(HookResult.Stop);
            
            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.Spec).Returns(HookRegistry.ToolExecuteBefore);
            handler2.Setup(h => h.Priority).Returns(20);

            _manager.Register(handler1.Object);
            _manager.Register(handler2.Object);

            var payload = HookPayload.Blocking(HookRegistry.ToolExecuteBefore, "test-session");
            var result = await _manager.TriggerAsync(payload);

            result.Continue.Should().BeFalse();
            handler1.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Once);
            handler2.Verify(h => h.ExecuteAsync(It.IsAny<HookPayload>()), Times.Never);
        }

        [Fact]
        public async Task TriggerAsync_ShouldReturnContinueTrue_WhenNoHandlers()
        {
            var payload = HookPayload.Blocking(
                new HookSpec(HookPolicy.Blocking, "nonexistent_hook"),
                "test-session");

            var result = await _manager.TriggerAsync(payload);

            result.Continue.Should().BeTrue();
        }

        [Fact]
        public void Register_ShouldIgnoreNullHandler()
        {
            _manager.Register(null!);

            _manager.Count(HookRegistry.ToolExecuteBefore).Should().Be(0);
        }
    }
}