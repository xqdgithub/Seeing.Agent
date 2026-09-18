using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Session.Tests
{
    public class ISessionStoreTests
    {
        [Fact]
        public void ISessionStore_HasExpected_Methods()
        {
            var type = typeof(Seeing.Session.Storage.ISessionStore);

            // Ensure required methods exist by name and arity (parameters count，含可选 CancellationToken)
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

            bool hasSaveAsync = methods.Any(m => m.Name == "SaveAsync" && m.GetParameters().Length == 2);
            bool hasLoadAsync = methods.Any(m => m.Name == "LoadAsync" && m.GetParameters().Length == 2);
            bool hasDeleteAsync = methods.Any(m => m.Name == "DeleteAsync" && m.GetParameters().Length == 2);
            bool hasListAsync = methods.Any(m => m.Name == "ListAsync" && m.GetParameters().Length == 1);
            bool hasQueryAsync = methods.Any(m => m.Name == "QueryAsync" && m.GetParameters().Length == 3);
            bool hasSaveAllAsync = methods.Any(m => m.Name == "SaveAllAsync" && m.GetParameters().Length == 2);
            bool hasLoadAllAsync = methods.Any(m => m.Name == "LoadAllAsync" && m.GetParameters().Length == 1);

            Assert.True(hasSaveAsync, "ISessionStore should declare SaveAsync(SessionData data, CancellationToken ct = default)");
            Assert.True(hasLoadAsync, "ISessionStore should declare LoadAsync(string sessionId, CancellationToken ct = default)");
            Assert.True(hasDeleteAsync, "ISessionStore should declare DeleteAsync(string sessionId, CancellationToken ct = default)");
            Assert.True(hasListAsync, "ISessionStore should declare ListAsync(CancellationToken ct = default)");
            Assert.True(hasQueryAsync, "ISessionStore should declare QueryAsync(string partitionId, string agentId, CancellationToken ct = default)");
            Assert.True(hasSaveAllAsync, "ISessionStore should declare SaveAllAsync(IEnumerable<SessionData> data, CancellationToken ct = default)");
            Assert.True(hasLoadAllAsync, "ISessionStore should declare LoadAllAsync(CancellationToken ct = default)");
        }

        [Fact]
        public void ISessionStore_EnumerationMethods_ShouldReturnIAsyncEnumerable()
        {
            var type = typeof(Seeing.Session.Storage.ISessionStore);
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

            var expected = typeof(IAsyncEnumerable<SessionData>);

            Assert.Equal(expected, methods.Single(m => m.Name == "ListAsync").ReturnType);
            Assert.Equal(expected, methods.Single(m => m.Name == "QueryAsync").ReturnType);
            Assert.Equal(expected, methods.Single(m => m.Name == "LoadAllAsync").ReturnType);
        }
    }
}
