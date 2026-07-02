using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Session.Compression;
using Seeing.Session.Core;
using Seeing.Session.Hooks;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests
{
    /// <summary>
    /// SessionManager 单元测试 - 新 SessionData 架构
    /// </summary>
    public class SessionManagerTests
    {
        private readonly Mock<ISessionStore> _mockStore;
        private readonly Mock<ICompressionStrategy> _mockCompressor;
        private readonly SessionHookManager _hookManager;
        private readonly SessionManager _sessionManager;

        public SessionManagerTests()
        {
            _mockStore = new Mock<ISessionStore>();
            _mockCompressor = new Mock<ICompressionStrategy>();
            _hookManager = new SessionHookManager(new NullLogger<SessionHookManager>());
            _sessionManager = new SessionManager(
                store: _mockStore.Object,
                compressor: _mockCompressor.Object,
                hookManager: _hookManager,
                logger: new NullLogger<SessionManager>());
        }

        // === Create 测试 ===

        [Fact]
        public void Create_ShouldCreateValidSession()
        {
            // Act
            var session = _sessionManager.Create();

            // Assert
            session.Should().NotBeNull();
            session.Id.Should().StartWith("ses_");
            session.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
            session.Status.Should().Be(SessionStatus.Created);
            session.SelectedAgent.Should().Be("primary");
            session.PartitionId.Should().Be("default");
        }

        [Fact]
        public void Create_WithPartitionId_ShouldSetPartitionId()
        {
            // Act
            var session = _sessionManager.Create(partitionId: "partition-001");

            // Assert
            session.PartitionId.Should().Be("partition-001");
        }

        [Fact]
        public void Create_WithSelectedAgent_ShouldSetSelectedAgent()
        {
            // Act
            var session = _sessionManager.Create(selectedAgent: "build");

            // Assert
            session.SelectedAgent.Should().Be("build");
        }

        [Fact]
        public void Create_WithAllParameters_ShouldSetAllFields()
        {
            // Act
            var session = _sessionManager.Create(partitionId: "test-part", selectedAgent: "oracle");

            // Assert
            session.PartitionId.Should().Be("test-part");
            session.SelectedAgent.Should().Be("oracle");
        }

        // === EnsureSessionAsync 测试 ===

        [Fact]
        public async Task EnsureSessionAsync_WhenInCache_ShouldReturnExisting()
        {
            // Arrange
            var created = _sessionManager.Create(selectedAgent: "build");
            _mockStore.Setup(s => s.LoadAsync(created.Id)).ReturnsAsync((SessionData?)null);

            // Act
            var result = await _sessionManager.EnsureSessionAsync(created.Id);

            // Assert
            result.Should().BeSameAs(created);
            _mockStore.Verify(s => s.LoadAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task EnsureSessionAsync_WhenInStore_ShouldLoadAndReturn()
        {
            // Arrange
            const string sessionId = "gateway-session-1";
            var storedSession = SessionData.Create("stored-part", "stored-agent");
            storedSession.Id = sessionId;
            _mockStore.Setup(s => s.LoadAsync(sessionId)).ReturnsAsync(storedSession);

            // Act
            var result = await _sessionManager.EnsureSessionAsync(sessionId);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().Be(sessionId);
            result.PartitionId.Should().Be("stored-part");
            result.SelectedAgent.Should().Be("stored-agent");
            _sessionManager.Get(sessionId).Should().BeSameAs(result);
            _mockStore.Verify(s => s.LoadAsync(sessionId), Times.Once);
        }

        [Fact]
        public async Task EnsureSessionAsync_WhenNotExists_ShouldCreateWithProvidedId()
        {
            // Arrange
            const string sessionId = "custom-gateway-id";
            _mockStore.Setup(s => s.LoadAsync(sessionId)).ReturnsAsync((SessionData?)null);

            // Act
            var result = await _sessionManager.EnsureSessionAsync(
                sessionId,
                selectedAgent: "oracle",
                partitionId: "gw-partition");

            // Assert
            result.Id.Should().Be(sessionId);
            result.Id.Should().NotStartWith("ses_");
            result.SelectedAgent.Should().Be("oracle");
            result.PartitionId.Should().Be("gw-partition");
            _sessionManager.Get(sessionId).Should().BeSameAs(result);
        }

        [Fact]
        public async Task EnsureSessionAsync_WhenCreating_ShouldTriggerCreatedHook()
        {
            // Arrange
            const string sessionId = "hook-test-id";
            var hookInvoked = false;
            _hookManager.AddHook(new TestCreatedHook(() => hookInvoked = true));
            _mockStore.Setup(s => s.LoadAsync(sessionId)).ReturnsAsync((SessionData?)null);

            // Act
            await _sessionManager.EnsureSessionAsync(sessionId);
            await Task.Delay(100);

            // Assert
            hookInvoked.Should().BeTrue();
        }

        [Fact]
        public async Task EnsureSessionAsync_WithEmptyId_ShouldThrow()
        {
            // Act
            var act = () => _sessionManager.EnsureSessionAsync(string.Empty);

            // Assert
            await act.Should().ThrowAsync<ArgumentException>()
                .WithParameterName("id");
        }

        private sealed class TestCreatedHook : ISessionHook
        {
            private readonly Action _onExecute;

            public TestCreatedHook(Action onExecute) => _onExecute = onExecute;

            public string HookPoint => HookPoints.Created;
            public int Priority => 0;

            public Task<SessionHookResult> ExecuteAsync(SessionHookContext context)
            {
                _onExecute();
                return Task.FromResult(new SessionHookResult());
            }
        }

        // === Get 测试 ===

        [Fact]
        public void Get_ShouldReturnExistingSession()
        {
            // Arrange
            var created = _sessionManager.Create();

            // Act
            var session = _sessionManager.Get(created.Id);

            // Assert
            session.Should().NotBeNull();
            session!.Id.Should().Be(created.Id);
        }

        [Fact]
        public void Get_WithInvalidId_ShouldReturnNull()
        {
            // Act
            var session = _sessionManager.Get("invalid-id");

            // Assert
            session.Should().BeNull();
        }

        [Fact]
        public void Get_WithNullOrEmptyId_ShouldReturnNull()
        {
            // Act & Assert
            _sessionManager.Get(null).Should().BeNull();
            _sessionManager.Get(string.Empty).Should().BeNull();
        }

        // === Delete 测试 ===

        [Fact]
        public void Delete_ShouldRemoveSession()
        {
            // Arrange
            var created = _sessionManager.Create();

            // Act
            var result = _sessionManager.Delete(created.Id);

            // Assert
            result.Should().BeTrue();
            _sessionManager.Get(created.Id).Should().BeNull();
        }

        [Fact]
        public void Delete_WithInvalidId_ShouldReturnFalse()
        {
            // Act
            var result = _sessionManager.Delete("invalid-id");

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void Delete_WithNullOrEmptyId_ShouldReturnFalse()
        {
            // Act & Assert
            _sessionManager.Delete(null).Should().BeFalse();
            _sessionManager.Delete(string.Empty).Should().BeFalse();
        }

        [Fact]
        public async Task Delete_ShouldCallStoreDeleteAsync()
        {
            // Arrange
            var created = _sessionManager.Create();
            _mockStore.Setup(s => s.DeleteAsync(created.Id)).Returns(Task.CompletedTask);

            // Act
            var result = _sessionManager.Delete(created.Id);

            // Assert
            result.Should().BeTrue();
            // 验证存储删除被调用（fire-and-forget，需等待）
            await Task.Delay(100);
            _mockStore.Verify(s => s.DeleteAsync(created.Id), Times.Once);
        }

        // === List 测试 ===

        [Fact]
        public void List_ShouldReturnAllSessions()
        {
            // Arrange
            _sessionManager.Create();
            _sessionManager.Create();
            _sessionManager.Create();

            // Act
            var sessions = _sessionManager.List();

            // Assert
            sessions.Should().HaveCount(3);
        }

        [Fact]
        public void List_WhenEmpty_ShouldReturnEmptyList()
        {
            // Arrange - no sessions created

            // Act
            var sessions = _sessionManager.List();

            // Assert
            sessions.Should().BeEmpty();
        }

        // === SaveAsync 测试 ===

        [Fact]
        public async Task SaveAsync_ShouldCallStoreSaveAsync()
        {
            // Arrange
            var created = _sessionManager.Create();
            _mockStore.Setup(s => s.SaveAsync(It.IsAny<SessionData>())).Returns(Task.CompletedTask);

            // Act
            await _sessionManager.SaveAsync(created.Id);

            // Assert
            _mockStore.Verify(s => s.SaveAsync(It.Is<SessionData>(d => d.Id == created.Id)), Times.Once);
        }

        [Fact]
        public async Task SaveAsync_WithInvalidId_ShouldNotCallStore()
        {
            // Act
            await _sessionManager.SaveAsync("invalid-id");

            // Assert
            _mockStore.Verify(s => s.SaveAsync(It.IsAny<SessionData>()), Times.Never);
        }

        [Fact]
        public async Task SaveAsync_WithoutStore_ShouldNotThrow()
        {
            // Arrange
            var managerWithoutStore = new SessionManager(store: null, logger: new NullLogger<SessionManager>());
            var created = managerWithoutStore.Create();

            // Act & Assert - should not throw
            await managerWithoutStore.SaveAsync(created.Id);
        }

        // === LoadAsync 测试 ===

        [Fact]
        public async Task LoadAsync_ShouldCallStoreLoadAsync()
        {
            // Arrange
            var storedSession = SessionData.Create("stored-part", "stored-agent");
            storedSession.Id = "stored-id";
            _mockStore.Setup(s => s.LoadAsync("stored-id")).ReturnsAsync(storedSession);

            // Act
            var result = await _sessionManager.LoadAsync("stored-id");

            // Assert
            result.Should().NotBeNull();
            result!.Id.Should().Be("stored-id");
            _mockStore.Verify(s => s.LoadAsync("stored-id"), Times.Once);
        }

        [Fact]
        public async Task LoadAsync_ShouldCacheSessionInMemory()
        {
            // Arrange
            var storedSession = SessionData.Create();
            storedSession.Id = "cached-id";
            _mockStore.Setup(s => s.LoadAsync("cached-id")).ReturnsAsync(storedSession);

            // Act
            await _sessionManager.LoadAsync("cached-id");
            var cached = _sessionManager.Get("cached-id");

            // Assert
            cached.Should().NotBeNull();
            cached!.Id.Should().Be("cached-id");
        }

        [Fact]
        public async Task LoadAsync_WhenStoreReturnsNull_ShouldReturnNull()
        {
            // Arrange
            _mockStore.Setup(s => s.LoadAsync("missing-id")).ReturnsAsync((SessionData?)null);

            // Act
            var result = await _sessionManager.LoadAsync("missing-id");

            // Assert
            result.Should().BeNull();
        }

        [Fact]
        public async Task LoadAsync_WithoutStore_ShouldReturnNull()
        {
            // Arrange
            var managerWithoutStore = new SessionManager(store: null, logger: new NullLogger<SessionManager>());

            // Act
            var result = await managerWithoutStore.LoadAsync("any-id");

            // Assert
            result.Should().BeNull();
        }

        // === Compress 测试 ===

        [Fact]
        public void Compress_ShouldCallCompressorCompress()
        {
            // Arrange
            var session = _sessionManager.Create();
            session.AddMessage(SessionMessage.SystemMessage("System prompt"));
            session.AddMessage(SessionMessage.UserMessage("Hello"));
            session.AddMessage(SessionMessage.AssistantMessage("Response"));

            var compressedMessages = new List<SessionMessage>
            {
                SessionMessage.SystemMessage("System prompt"),
                SessionMessage.UserMessage("Hello")
            };
            _mockCompressor.Setup(c => c.Compress(It.IsAny<IReadOnlyList<SessionMessage>>()))
                .Returns(compressedMessages);

            // Act
            var result = _sessionManager.Compress(session.Id);

            // Assert
            result.Should().HaveCount(2);
            _mockCompressor.Verify(c => c.Compress(It.IsAny<IReadOnlyList<SessionMessage>>()), Times.Once);
        }

        [Fact]
        public void Compress_ShouldUpdateSessionMessages()
        {
            // Arrange
            var session = _sessionManager.Create();
            session.AddMessage(SessionMessage.SystemMessage("System"));
            for (int i = 0; i < 30; i++)
            {
                session.AddMessage(SessionMessage.UserMessage($"Message {i}"));
            }

            var compressedMessages = new List<SessionMessage>
            {
                SessionMessage.SystemMessage("System"),
                SessionMessage.UserMessage("Message 29")
            };
            _mockCompressor.Setup(c => c.Compress(It.IsAny<IReadOnlyList<SessionMessage>>()))
                .Returns(compressedMessages);

            // Act
            _sessionManager.Compress(session.Id);

            // Assert
            session.Messages.Should().HaveCount(2);
            session.Messages[0].Role.Should().Be(MessageRole.System);
        }

        [Fact]
        public void Compress_WithInvalidId_ShouldReturnEmpty()
        {
            // Act
            var result = _sessionManager.Compress("invalid-id");

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public void Compress_WhenSessionHasNoMessages_ShouldReturnEmpty()
        {
            // Arrange
            var session = _sessionManager.Create();

            // Act
            var result = _sessionManager.Compress(session.Id);

            // Assert
            result.Should().BeEmpty();
        }

        [Fact]
        public void Compress_WithoutCompressor_ShouldReturnOriginalMessages()
        {
            // Arrange
            var managerWithoutCompressor = new SessionManager(compressor: null, logger: new NullLogger<SessionManager>());
            var session = managerWithoutCompressor.Create();
            session.AddMessage(SessionMessage.UserMessage("Test"));

            // Act
            var result = managerWithoutCompressor.Compress(session.Id);

            // Assert
            result.Should().HaveCount(1);
            result[0].Content.Should().Be("Test");
        }

        // === 无注入组件测试 ===

        [Fact]
        public void SessionManager_WithNullDependencies_ShouldWorkForBasicOperations()
        {
            // Arrange
            var minimalManager = new SessionManager();

            // Act
            var session = minimalManager.Create();
            var retrieved = minimalManager.Get(session.Id);
            var listed = minimalManager.List();
            var deleted = minimalManager.Delete(session.Id);

            // Assert
            session.Should().NotBeNull();
            retrieved.Should().NotBeNull();
            listed.Should().Contain(session);
            deleted.Should().BeTrue();
        }

        // === SessionData 内置操作测试 ===

        [Fact]
        public void SessionData_AddMessage_ShouldUpdateMessageCount()
        {
            // Arrange
            var session = _sessionManager.Create();

            // Act
            session.AddMessage(SessionMessage.UserMessage("Hello"));

            // Assert
            session.MessageCount.Should().Be(1);
            session.Messages[0].Content.Should().Be("Hello");
            session.Messages[0].Role.Should().Be(MessageRole.User);
        }

        [Fact]
        public void SessionData_SetContext_ShouldStoreValue()
        {
            // Arrange
            var session = _sessionManager.Create();

            // Act
            session.SetContext("key1", "value1");

            // Assert
            var value = session.GetContext<string>("key1");
            value.Should().Be("value1");
        }

        [Fact]
        public void SessionData_GetContext_WithWrongType_ShouldReturnDefault()
        {
            // Arrange
            var session = _sessionManager.Create();
            session.SetContext("key1", "string-value");

            // Act
            var value = session.GetContext<int>("key1");

            // Assert
            value.Should().Be(0); // default int
        }

        [Fact]
        public void SessionData_Clone_ShouldCreateDeepCopy()
        {
            // Arrange
            var session = _sessionManager.Create();
            session.AddMessage(SessionMessage.UserMessage("Test"));
            session.SetContext("key", "value");

            // Act
            var clone = session.Clone();

            // Assert
            clone.Id.Should().Be(session.Id);
            clone.Messages.Should().HaveCount(1);
            clone.GetContext<string>("key").Should().Be("value");

            // 验证是深拷贝
            clone.AddMessage(SessionMessage.UserMessage("New"));
            session.Messages.Should().HaveCount(1); // 原对象不受影响
            clone.Messages.Should().HaveCount(2);
        }
    }
}