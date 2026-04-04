using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Hooks;
using Xunit;

namespace Seeing.Agent.Tests.Hooks
{
    /// <summary>
    /// HookManager 单元测试
    /// </summary>
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
        public void RegisterHandler_ShouldAddHandler()
        {
            var handler = new Mock<IHookHandler>();
            handler.Setup(h => h.HookPoint).Returns(HookPoints.ToolExecuteBefore);
            handler.Setup(h => h.Priority).Returns(10);

            _manager.RegisterHandler(handler.Object);

            _manager.GetHandlerCount(HookPoints.ToolExecuteBefore).Should().Be(1);
        }

        [Fact]
        public void RegisterHandler_ShouldOrderByPriority()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.HookPoint).Returns(HookPoints.ChatBeforeStart);
            handler1.Setup(h => h.Priority).Returns(20);
            
            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.HookPoint).Returns(HookPoints.ChatBeforeStart);
            handler2.Setup(h => h.Priority).Returns(10);

            _manager.RegisterHandler(handler1.Object);
            _manager.RegisterHandler(handler2.Object);

            _manager.GetHandlerCount(HookPoints.ChatBeforeStart).Should().Be(2);
        }

        [Fact]
        public async Task TriggerAsync_ShouldCallAllHandlers()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.HookPoint).Returns(HookPoints.ToolExecuteBefore);
            handler1.Setup(h => h.Priority).Returns(10);
            handler1.Setup(h => h.ExecuteAsync(It.IsAny<HookContext>()))
                .ReturnsAsync(new HookResult { Continue = true });
            
            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.HookPoint).Returns(HookPoints.ToolExecuteBefore);
            handler2.Setup(h => h.Priority).Returns(20);
            handler2.Setup(h => h.ExecuteAsync(It.IsAny<HookContext>()))
                .ReturnsAsync(new HookResult { Continue = true });

            _manager.RegisterHandler(handler1.Object);
            _manager.RegisterHandler(handler2.Object);

            var result = await _manager.TriggerAsync(HookPoints.ToolExecuteBefore, new Dictionary<string, object>());

            result.Continue.Should().BeTrue();
            handler1.Verify(h => h.ExecuteAsync(It.IsAny<HookContext>()), Times.Once);
            handler2.Verify(h => h.ExecuteAsync(It.IsAny<HookContext>()), Times.Once);
        }

        [Fact]
        public async Task TriggerAsync_ShouldStopWhenContinueIsFalse()
        {
            var handler1 = new Mock<IHookHandler>();
            handler1.Setup(h => h.HookPoint).Returns(HookPoints.ToolExecuteBefore);
            handler1.Setup(h => h.Priority).Returns(10);
            handler1.Setup(h => h.ExecuteAsync(It.IsAny<HookContext>()))
                .ReturnsAsync(new HookResult { Continue = false });
            
            var handler2 = new Mock<IHookHandler>();
            handler2.Setup(h => h.HookPoint).Returns(HookPoints.ToolExecuteBefore);
            handler2.Setup(h => h.Priority).Returns(20);

            _manager.RegisterHandler(handler1.Object);
            _manager.RegisterHandler(handler2.Object);

            var result = await _manager.TriggerAsync(HookPoints.ToolExecuteBefore);

            result.Continue.Should().BeFalse();
            handler1.Verify(h => h.ExecuteAsync(It.IsAny<HookContext>()), Times.Once);
            handler2.Verify(h => h.ExecuteAsync(It.IsAny<HookContext>()), Times.Never);
        }

        [Fact]
        public async Task TriggerAsync_ShouldReturnContinueTrue_WhenNoHandlers()
        {
            var result = await _manager.TriggerAsync("nonexistent_hook");

            result.Continue.Should().BeTrue();
        }

        [Fact]
        public void RegisterHandler_ShouldIgnoreNullHandler()
        {
            _manager.RegisterHandler(null!);

            _manager.GetHandlerCount(HookPoints.ToolExecuteBefore).Should().Be(0);
        }
    }
}