using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests
{
    // In-memory ISessionStore implementation for tests
    internal class InMemorySessionStore : ISessionStore
    {
        private readonly Dictionary<string, SessionData> _store = new Dictionary<string, SessionData>();

        public Task SaveAsync(SessionData data)
        {
            _store[data.Id] = data;
            return Task.CompletedTask;
        }

        public Task<SessionData?> LoadAsync(string sessionId)
        {
            _store.TryGetValue(sessionId, out var data);
            return Task.FromResult<SessionData?>(data);
        }

        public Task DeleteAsync(string sessionId)
        {
            _store.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task<IAsyncEnumerable<SessionData>> ListAsync()
        {
            IAsyncEnumerable<SessionData> Enumerate()
            {
                async IAsyncEnumerable<SessionData> Gen()
                {
                    foreach (var d in _store.Values)
                        yield return d;
                }
                return Gen();
            }
            return Task.FromResult(Enumerate());
        }

        public Task<IAsyncEnumerable<SessionData>> QueryAsync(string partitionId, string agentId)
        {
            return Task.FromResult<IAsyncEnumerable<SessionData>>(ListAsync().Result);
        }

        public Task SaveAllAsync(IEnumerable<SessionData> data)
        {
            foreach (var d in data)
            {
                _store[d.Id] = d;
            }
            return Task.CompletedTask;
        }

        public Task<IAsyncEnumerable<SessionData>> LoadAllAsync()
        {
            IAsyncEnumerable<SessionData> EnumerateAll()
            {
                async IAsyncEnumerable<SessionData> Gen()
                {
                    foreach (var d in _store.Values)
                        yield return d;
                }
                return Gen();
            }
            return Task.FromResult(EnumerateAll());
        }
    }

    public class SessionFactoryTests
    {
        [Fact]
        public async Task CreateAsync_ShouldCreateNewSession_WithProvidedMetadata()
        {
            var store = new InMemorySessionStore();
            var factory = new SessionFactory(store);

            var sess = await factory.CreateAsync("Title1", "partition-1", "agent-42");

            Assert.NotNull(sess);
            Assert.False(string.IsNullOrEmpty(sess.Id));
            Assert.Equal("Title1", sess.Title);
            Assert.Equal("partition-1", sess.PartitionId);
        }

        [Fact]
        public async Task CloneAsync_ShouldDeepCopyAndAssignNewId()
        {
            var store = new InMemorySessionStore();
            var factory = new SessionFactory(store);

            var original = await factory.CreateAsync("Orig", "p1", "a1");
            // set some state to verify deep copy
            original.SetState("messages", "[{\"text\":\"hello\"}]");
            await original.SaveAsync();

            var newId = Guid.NewGuid().ToString();
            var clone = await factory.CloneAsync(original.Id, newId);

            Assert.Equal(newId, clone.Id);
            Assert.Equal(original.Title, clone.Title);
            Assert.Equal(original.PartitionId, clone.PartitionId);
            // Verify deep copy of state (messages)
            var messages = clone.GetState<string>("messages");
            Assert.Equal("[{\"text\":\"hello\"}]", messages);
        }

        [Fact]
        public async Task ResumeAsync_ShouldLoadExistingSession()
        {
            var store = new InMemorySessionStore();
            var factory = new SessionFactory(store);

            var sess = await factory.CreateAsync("T", "prt", "ag");
            await sess.SaveAsync();

            var resumed = await factory.ResumeAsync(sess.Id);
            Assert.Equal(sess.Id, resumed.Id);
            Assert.Equal(sess.Title, resumed.Title);
        }
    }
}
