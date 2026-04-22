using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Session.Core;
using Seeing.Session.Hooks;
using Xunit;

namespace Seeing.Session.Tests.Hooks
{
    public class SessionHookManagerTests
    {
        #region AddHook Tests

        [Fact]
        public void AddHook_ShouldAddHookToList()
        {
            var manager = new SessionHookManager();
            var hook = new Mock<ISessionHook>();
            hook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook.Setup(h => h.Priority).Returns(10);

            manager.AddHook(hook.Object);

            manager.GetHookCount(SessionHookPoints.Created).Should().Be(1);
        }

        [Fact]
        public void AddHook_ShouldSortByPriority()
        {
            var manager = new SessionHookManager();
            
            var hook1 = new Mock<ISessionHook>();
            hook1.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook1.Setup(h => h.Priority).Returns(20);

            var hook2 = new Mock<ISessionHook>();
            hook2.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook2.Setup(h => h.Priority).Returns(10);

            manager.AddHook(hook1.Object);
            manager.AddHook(hook2.Object);

            manager.GetHookCount(SessionHookPoints.Created).Should().Be(2);
        }

        [Fact]
        public void AddHook_ShouldIgnoreNullHook()
        {
            var manager = new SessionHookManager();
            
            manager.AddHook(null!);

            manager.GetHookCount(SessionHookPoints.Created).Should().Be(0);
        }

        [Fact]
        public void AddHook_ShouldSupportMultipleHookPoints()
        {
            var manager = new SessionHookManager();
            
            var createdHook = new Mock<ISessionHook>();
            createdHook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            createdHook.Setup(h => h.Priority).Returns(10);

            var savedHook = new Mock<ISessionHook>();
            savedHook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Saved);
            savedHook.Setup(h => h.Priority).Returns(10);

            manager.AddHook(createdHook.Object);
            manager.AddHook(savedHook.Object);

            manager.GetHookCount(SessionHookPoints.Created).Should().Be(1);
            manager.GetHookCount(SessionHookPoints.Saved).Should().Be(1);
        }

        #endregion

        #region RemoveHook Tests

        [Fact]
        public void RemoveHook_ShouldRemoveHook()
        {
            var manager = new SessionHookManager();
            var hook = new Mock<ISessionHook>();
            hook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook.Setup(h => h.Priority).Returns(10);

            manager.AddHook(hook.Object);
            var result = manager.RemoveHook(hook.Object);

            result.Should().BeTrue();
            manager.GetHookCount(SessionHookPoints.Created).Should().Be(0);
        }

        [Fact]
        public void RemoveHook_ShouldReturnFalseForNonExistentHook()
        {
            var manager = new SessionHookManager();
            var hook = new Mock<ISessionHook>();
            hook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook.Setup(h => h.Priority).Returns(10);

            var result = manager.RemoveHook(hook.Object);

            result.Should().BeFalse();
        }

        [Fact]
        public void RemoveHook_ShouldReturnFalseForNullHook()
        {
            var manager = new SessionHookManager();

            var result = manager.RemoveHook(null!);

            result.Should().BeFalse();
        }

        #endregion

        #region ClearHooks Tests

        [Fact]
        public void ClearHooks_ShouldRemoveAllHooksForPoint()
        {
            var manager = new SessionHookManager();
            
            var hook1 = new Mock<ISessionHook>();
            hook1.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook1.Setup(h => h.Priority).Returns(10);

            var hook2 = new Mock<ISessionHook>();
            hook2.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook2.Setup(h => h.Priority).Returns(20);

            manager.AddHook(hook1.Object);
            manager.AddHook(hook2.Object);
            var result = manager.ClearHooks(SessionHookPoints.Created);

            result.Should().BeTrue();
            manager.GetHookCount(SessionHookPoints.Created).Should().Be(0);
        }

        [Fact]
        public void ClearHooks_ShouldReturnFalseForNonExistentPoint()
        {
            var manager = new SessionHookManager();

            var result = manager.ClearHooks(SessionHookPoints.Created);

            result.Should().BeFalse();
        }

        #endregion

        #region GetHookCount Tests

        [Fact]
        public void GetHookCount_ShouldReturnZeroForEmptyManager()
        {
            var manager = new SessionHookManager();

            manager.GetHookCount(SessionHookPoints.Created).Should().Be(0);
        }

        [Fact]
        public void GetHookCount_ShouldReturnCorrectCount()
        {
            var manager = new SessionHookManager();
            
            var hook1 = new Mock<ISessionHook>();
            hook1.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook1.Setup(h => h.Priority).Returns(10);

            var hook2 = new Mock<ISessionHook>();
            hook2.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook2.Setup(h => h.Priority).Returns(20);

            manager.AddHook(hook1.Object);
            manager.AddHook(hook2.Object);

            manager.GetHookCount(SessionHookPoints.Created).Should().Be(2);
        }

        #endregion

        #region TriggerAsync Tests

        [Fact]
        public async Task TriggerAsync_ShouldNotThrowWhenNoHooks()
        {
            var manager = new SessionHookManager();

            await manager.TriggerAsync(SessionHookPoints.Created);
            // Should complete without throwing
        }

        [Fact]
        public async Task TriggerAsync_ShouldExecuteHook()
        {
            var manager = new SessionHookManager();
            var hook = new Mock<ISessionHook>();
            hook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook.Setup(h => h.Priority).Returns(10);
            hook.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .ReturnsAsync(new SessionHookResult { Continue = true });

            manager.AddHook(hook.Object);
            await manager.TriggerAsync(SessionHookPoints.Created);

            // Wait for async task to complete
            await Task.Delay(100);

            hook.Verify(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()), Times.Once());
        }

        [Fact]
        public async Task TriggerAsync_ShouldPassCorrectContext()
        {
            var manager = new SessionHookManager();
            var hook = new Mock<ISessionHook>();
            hook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook.Setup(h => h.Priority).Returns(10);
            hook.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .ReturnsAsync(new SessionHookResult { Continue = true });

            var session = new SessionData { Id = "test-123", Title = "Test Session" };
            manager.AddHook(hook.Object);
            await manager.TriggerAsync(SessionHookPoints.Created, session);

            // Wait for async task to complete
            await Task.Delay(100);

            hook.Verify(h => h.ExecuteAsync(It.Is<SessionHookContext>(ctx =>
                ctx.HookPoint == SessionHookPoints.Created &&
                ctx.SessionId == "test-123" &&
                ctx.Session == session
            )), Times.Once());
        }

        [Fact]
        public async Task TriggerAsync_ShouldPassSessionId()
        {
            var manager = new SessionHookManager();
            var hook = new Mock<ISessionHook>();
            hook.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook.Setup(h => h.Priority).Returns(10);
            hook.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .ReturnsAsync(new SessionHookResult { Continue = true });

            manager.AddHook(hook.Object);
            await manager.TriggerAsync(SessionHookPoints.Created, "session-456");

            // Wait for async task to complete
            await Task.Delay(100);

            hook.Verify(h => h.ExecuteAsync(It.Is<SessionHookContext>(ctx =>
                ctx.HookPoint == SessionHookPoints.Created &&
                ctx.SessionId == "session-456"
            )), Times.Once());
        }

        [Fact]
        public async Task TriggerAsync_ShouldExecuteHooksInPriorityOrder()
        {
            var manager = new SessionHookManager();
            var executionOrder = new List<int>();

            var hook1 = new Mock<ISessionHook>();
            hook1.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook1.Setup(h => h.Priority).Returns(20);
            hook1.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .Callback(() => executionOrder.Add(20))
                .ReturnsAsync(new SessionHookResult { Continue = true });

            var hook2 = new Mock<ISessionHook>();
            hook2.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook2.Setup(h => h.Priority).Returns(10);
            hook2.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .Callback(() => executionOrder.Add(10))
                .ReturnsAsync(new SessionHookResult { Continue = true });

            manager.AddHook(hook1.Object);
            manager.AddHook(hook2.Object);
            await manager.TriggerAsync(SessionHookPoints.Created);

            // Wait for async task to complete
            await Task.Delay(100);

            // Priority 10 should execute first, then 20
            executionOrder.Should().Equal(10, 20);
        }

        [Fact]
        public async Task TriggerAsync_ShouldStopChainWhenContinueIsFalse()
        {
            var manager = new SessionHookManager();
            var executionOrder = new List<int>();

            var hook1 = new Mock<ISessionHook>();
            hook1.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook1.Setup(h => h.Priority).Returns(10);
            hook1.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .Callback(() => executionOrder.Add(10))
                .ReturnsAsync(new SessionHookResult { Continue = false });

            var hook2 = new Mock<ISessionHook>();
            hook2.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook2.Setup(h => h.Priority).Returns(20);
            hook2.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .Callback(() => executionOrder.Add(20))
                .ReturnsAsync(new SessionHookResult { Continue = true });

            manager.AddHook(hook1.Object);
            manager.AddHook(hook2.Object);
            await manager.TriggerAsync(SessionHookPoints.Created);

            // Wait for async task to complete
            await Task.Delay(100);

            executionOrder.Should().Equal(10); // Only hook1 executed
            hook2.Verify(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()), Times.Never());
        }

        [Fact]
        public async Task TriggerAsync_ShouldContinueOnError()
        {
            var manager = new SessionHookManager();
            var executionOrder = new List<int>();

            var hook1 = new Mock<ISessionHook>();
            hook1.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook1.Setup(h => h.Priority).Returns(10);
            hook1.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .Callback(() => executionOrder.Add(10))
                .ThrowsAsync(new Exception("Hook error"));

            var hook2 = new Mock<ISessionHook>();
            hook2.Setup(h => h.HookPoint).Returns(SessionHookPoints.Created);
            hook2.Setup(h => h.Priority).Returns(20);
            hook2.Setup(h => h.ExecuteAsync(It.IsAny<SessionHookContext>()))
                .Callback(() => executionOrder.Add(20))
                .ReturnsAsync(new SessionHookResult { Continue = true });

            manager.AddHook(hook1.Object);
            manager.AddHook(hook2.Object);
            await manager.TriggerAsync(SessionHookPoints.Created);

            // Wait for async task to complete
            await Task.Delay(100);

            // Both should execute, hook1 throws but chain continues
            executionOrder.Should().Equal(10, 20);
        }

        #endregion

        #region Hook Points Tests

        [Fact]
        public void SessionHookPoints_ShouldHaveAllExpectedPoints()
        {
            SessionHookPoints.Created.Should().Be("session.created");
            SessionHookPoints.Saving.Should().Be("session.saving");
            SessionHookPoints.Saved.Should().Be("session.saved");
            SessionHookPoints.Loading.Should().Be("session.loading");
            SessionHookPoints.Loaded.Should().Be("session.loaded");
            SessionHookPoints.Destroyed.Should().Be("session.destroyed");
        }

        #endregion

        #region Constructor Tests

        [Fact]
        public void Constructor_ShouldWorkWithoutLogger()
        {
            var manager = new SessionHookManager();
            manager.Should().NotBeNull();
        }

        [Fact]
        public void Constructor_ShouldWorkWithLogger()
        {
            var logger = new Mock<ILogger<SessionHookManager>>();
            var manager = new SessionHookManager(logger.Object);
            manager.Should().NotBeNull();
        }

        #endregion
    }
}