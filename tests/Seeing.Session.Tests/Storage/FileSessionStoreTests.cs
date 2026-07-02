using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage
{
    public class FileSessionStoreTests : IDisposable
    {
        private readonly string _testDirectory;
        private readonly FileSessionStore _store;

        public FileSessionStoreTests()
        {
            // 使用临时目录进行测试
            _testDirectory = Path.Combine(
                Path.GetTempPath(),
                "Seeing.Session.Tests",
                Guid.NewGuid().ToString("N"));
            
            _store = new FileSessionStore(_testDirectory);
        }

        public void Dispose()
        {
            // 清理测试目录
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }

        #region SaveAsync Tests

        [Fact]
        public async Task SaveAsync_Should_Create_File()
        {
            var session = CreateTestSession("test-session-1");
            
            await _store.SaveAsync(session);
            
            var filePath = Path.Combine(_testDirectory, "test-session-1.json");
            File.Exists(filePath).Should().BeTrue();
        }

        [Fact]
        public async Task SaveAsync_Should_Update_UpdatedAt()
        {
            var session = CreateTestSession("test-session-2");
            var originalUpdatedAt = session.UpdatedAt;
            
            await _store.SaveAsync(session);
            
            session.UpdatedAt.Should().NotBe(originalUpdatedAt);
        }

        [Fact]
        public async Task SaveAsync_Should_Set_CreatedAt_If_Default()
        {
            var session = CreateTestSession("test-session-3");
            session.CreatedAt = default;
            
            await _store.SaveAsync(session);
            
            session.CreatedAt.Should().NotBe(default);
        }

        [Fact]
        public async Task SaveAsync_Should_Throw_On_Null_Data()
        {
            var act = () => _store.SaveAsync(null!);
            
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        [Fact]
        public async Task SaveAsync_Should_Throw_On_Empty_SessionId()
        {
            var session = CreateTestSession("");
            
            var act = () => _store.SaveAsync(session);
            
            await act.Should().ThrowAsync<ArgumentException>();
        }

        #endregion

        #region LoadAsync Tests

        [Fact]
        public async Task LoadAsync_Should_Return_Session_If_Exists()
        {
            var session = CreateTestSession("test-session-load-1");
            await _store.SaveAsync(session);
            
            var loaded = await _store.LoadAsync("test-session-load-1");
            
            loaded.Should().NotBeNull();
            loaded!.Id.Should().Be("test-session-load-1");
        }

        [Fact]
        public async Task LoadAsync_Should_Return_Null_If_Not_Exists()
        {
            var loaded = await _store.LoadAsync("non-existent-session");
            
            loaded.Should().BeNull();
        }

        [Fact]
        public async Task LoadAsync_Should_Throw_On_Empty_SessionId()
        {
            var act = () => _store.LoadAsync("");
            
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [Fact]
        public async Task LoadAsync_Should_Throw_SessionLoadException_On_Corrupted_File()
        {
            var sessionId = "corrupted-session";
            var filePath = Path.Combine(_testDirectory, $"{sessionId}.json");
            
            // 创建损坏的 JSON 文件
            await File.WriteAllTextAsync(filePath, "{ invalid json content }");
            
            var act = () => _store.LoadAsync(sessionId);
            
            await act.Should().ThrowAsync<SessionLoadException>()
                .Where(ex => ex.SessionId == sessionId && ex.FilePath == filePath);
        }

        [Fact]
        public async Task LoadAsync_Should_Throw_SessionLoadException_On_Empty_File()
        {
            var sessionId = "empty-session";
            var filePath = Path.Combine(_testDirectory, $"{sessionId}.json");
            
            // 创建空文件
            await File.WriteAllTextAsync(filePath, "");
            
            var act = () => _store.LoadAsync(sessionId);
            
            await act.Should().ThrowAsync<SessionLoadException>()
                .Where(ex => ex.SessionId == sessionId && ex.FilePath == filePath);
        }

        #endregion

        #region DeleteAsync Tests

        [Fact]
        public async Task DeleteAsync_Should_Remove_File()
        {
            var session = CreateTestSession("test-session-delete-1");
            await _store.SaveAsync(session);
            
            await _store.DeleteAsync("test-session-delete-1");
            
            var filePath = Path.Combine(_testDirectory, "test-session-delete-1.json");
            File.Exists(filePath).Should().BeFalse();
        }

        [Fact]
        public async Task DeleteAsync_Should_Not_Throw_If_Not_Exists()
        {
            var act = () => _store.DeleteAsync("non-existent-session");
            
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task DeleteAsync_Should_Throw_On_Empty_SessionId()
        {
            var act = () => _store.DeleteAsync("");
            
            await act.Should().ThrowAsync<ArgumentException>();
        }

        #endregion

        #region ListAsync Tests

        [Fact]
        public async Task ListAsync_Should_Return_All_Sessions()
        {
            await _store.SaveAsync(CreateTestSession("session-1"));
            await _store.SaveAsync(CreateTestSession("session-2"));
            await _store.SaveAsync(CreateTestSession("session-3"));
            
            var sessions = await _store.ListAsync();
            var count = 0;
            
            await foreach (var session in sessions)
            {
                count++;
            }
            
            count.Should().Be(3);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Empty_If_No_Sessions()
        {
            var sessions = await _store.ListAsync();
            var count = 0;
            
            await foreach (var session in sessions)
            {
                count++;
            }
            
            count.Should().Be(0);
        }

        [Fact]
        public async Task ListAsync_Should_Skip_Corrupted_Files()
        {
            // 创建正常会话
            await _store.SaveAsync(CreateTestSession("valid-session"));
            
            // 创建损坏文件
            var corruptedPath = Path.Combine(_testDirectory, "corrupted.json");
            await File.WriteAllTextAsync(corruptedPath, "{ bad json }");
            
            var sessions = await _store.ListAsync();
            var ids = new System.Collections.Generic.List<string>();
            
            await foreach (var session in sessions)
            {
                ids.Add(session.Id);
            }
            
            ids.Should().Contain("valid-session");
            ids.Should().NotContain("corrupted");
        }

        #endregion

        #region QueryAsync Tests

        [Fact]
        public async Task QueryAsync_Should_Filter_By_PartitionId()
        {
            var session1 = CreateTestSession("q-session-1");
            session1.PartitionId = "partition-a";
            
            var session2 = CreateTestSession("q-session-2");
            session2.PartitionId = "partition-b";
            
            await _store.SaveAsync(session1);
            await _store.SaveAsync(session2);
            
            var sessions = await _store.QueryAsync("partition-a", null);
            var ids = new System.Collections.Generic.List<string>();
            
            await foreach (var session in sessions)
            {
                ids.Add(session.Id);
            }
            
            ids.Should().Contain("q-session-1");
            ids.Should().NotContain("q-session-2");
        }

        [Fact]
        public async Task QueryAsync_Should_Filter_By_AgentId()
        {
            var session1 = CreateTestSession("q-agent-1");
            session1.Agent = new AgentMetadata { AgentId = "agent-x", AgentName = "Agent X" };
            
            var session2 = CreateTestSession("q-agent-2");
            session2.Agent = new AgentMetadata { AgentId = "agent-y", AgentName = "Agent Y" };
            
            await _store.SaveAsync(session1);
            await _store.SaveAsync(session2);
            
            var sessions = await _store.QueryAsync(null, "agent-x");
            var ids = new System.Collections.Generic.List<string>();
            
            await foreach (var session in sessions)
            {
                ids.Add(session.Id);
            }
            
            ids.Should().Contain("q-agent-1");
            ids.Should().NotContain("q-agent-2");
        }

        [Fact]
        public async Task QueryAsync_Should_Filter_By_Both()
        {
            var session1 = CreateTestSession("q-both-1");
            session1.PartitionId = "p1";
            session1.Agent = new AgentMetadata { AgentId = "a1" };
            
            var session2 = CreateTestSession("q-both-2");
            session2.PartitionId = "p1";
            session2.Agent = new AgentMetadata { AgentId = "a2" };
            
            var session3 = CreateTestSession("q-both-3");
            session3.PartitionId = "p2";
            session3.Agent = new AgentMetadata { AgentId = "a1" };
            
            await _store.SaveAsync(session1);
            await _store.SaveAsync(session2);
            await _store.SaveAsync(session3);
            
            var sessions = await _store.QueryAsync("p1", "a1");
            var ids = new System.Collections.Generic.List<string>();
            
            await foreach (var session in sessions)
            {
                ids.Add(session.Id);
            }
            
            ids.Should().Contain("q-both-1");
            ids.Should().NotContain("q-both-2");
            ids.Should().NotContain("q-both-3");
        }

        #endregion

        #region SaveAllAsync Tests

        [Fact]
        public async Task SaveAllAsync_Should_Save_Multiple_Sessions()
        {
            var sessions = new[]
            {
                CreateTestSession("batch-1"),
                CreateTestSession("batch-2"),
                CreateTestSession("batch-3")
            };
            
            await _store.SaveAllAsync(sessions);
            
            var filePath1 = Path.Combine(_testDirectory, "batch-1.json");
            var filePath2 = Path.Combine(_testDirectory, "batch-2.json");
            var filePath3 = Path.Combine(_testDirectory, "batch-3.json");
            
            File.Exists(filePath1).Should().BeTrue();
            File.Exists(filePath2).Should().BeTrue();
            File.Exists(filePath3).Should().BeTrue();
        }

        [Fact]
        public async Task SaveAllAsync_Should_Throw_On_Null()
        {
            var act = () => _store.SaveAllAsync(null!);
            
            await act.Should().ThrowAsync<ArgumentNullException>();
        }

        #endregion

        #region LoadAllAsync Tests

        [Fact]
        public async Task LoadAllAsync_Should_Return_All_Sessions()
        {
            await _store.SaveAsync(CreateTestSession("load-all-1"));
            await _store.SaveAsync(CreateTestSession("load-all-2"));
            
            var sessions = await _store.LoadAllAsync();
            var count = 0;
            
            await foreach (var session in sessions)
            {
                count++;
            }
            
            count.Should().Be(2);
        }

        #endregion

        #region SessionLoadException Tests

        [Fact]
        public void SessionLoadException_Should_Store_SessionId_And_FilePath()
        {
            var ex = new SessionLoadException("session-123", "/path/to/file.json", "Test message");
            
            ex.SessionId.Should().Be("session-123");
            ex.FilePath.Should().Be("/path/to/file.json");
            ex.Message.Should().Be("Test message");
        }

        [Fact]
        public void SessionLoadException_Should_Store_InnerException()
        {
            var inner = new InvalidOperationException("Inner error");
            var ex = new SessionLoadException("session-123", "/path/to/file.json", "Test message", inner);
            
            ex.InnerException.Should().Be(inner);
        }

        #endregion

        #region Helper Methods

        private SessionData CreateTestSession(string id)
        {
            return new SessionData
            {
                Id = id,
                Title = $"Test Session {id}",
                PartitionId = "default",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Agent = new AgentMetadata
                {
                    AgentId = "test-agent",
                    AgentName = "Test Agent",
                    Role = "assistant"
                },
                Status = SessionStatus.Active
            };
        }

        #endregion
    }
}