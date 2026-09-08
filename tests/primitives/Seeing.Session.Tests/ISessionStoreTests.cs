using System.Linq;
using System.Reflection;
using Xunit;

namespace Seeing.Session.Tests
{
    public class ISessionStoreTests
    {
        [Fact]
        public void ISessionStore_HasExpected_Methods()
        {
            var type = typeof(Seeing.Session.Storage.ISessionStore);

            // Ensure required methods exist by name and arity (parameters count)
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);

            bool hasSaveAsync = methods.Any(m => m.Name == "SaveAsync" && m.GetParameters().Length == 1);
            bool hasLoadAsync = methods.Any(m => m.Name == "LoadAsync" && m.GetParameters().Length == 1);
            bool hasDeleteAsync = methods.Any(m => m.Name == "DeleteAsync" && m.GetParameters().Length == 1);
            bool hasListAsync = methods.Any(m => m.Name == "ListAsync" && m.GetParameters().Length == 0);
            bool hasQueryAsync = methods.Any(m => m.Name == "QueryAsync" && m.GetParameters().Length == 2);
            bool hasSaveAllAsync = methods.Any(m => m.Name == "SaveAllAsync" && m.GetParameters().Length == 1);
            bool hasLoadAllAsync = methods.Any(m => m.Name == "LoadAllAsync" && m.GetParameters().Length == 0 || m.GetParameters().Length == 1);

            Assert.True(hasSaveAsync, "ISessionStore should declare SaveAsync(SessionData data)");
            Assert.True(hasLoadAsync, "ISessionStore should declare LoadAsync(string sessionId)");
            Assert.True(hasDeleteAsync, "ISessionStore should declare DeleteAsync(string sessionId)");
            Assert.True(hasListAsync, "ISessionStore should declare ListAsync()");
            Assert.True(hasQueryAsync, "ISessionStore should declare QueryAsync(string partitionId, string agentId)");
            Assert.True(hasSaveAllAsync, "ISessionStore should declare SaveAllAsync(IEnumerable<SessionData> data)");
            Assert.True(hasLoadAllAsync, "ISessionStore should declare LoadAllAsync()");
        }
    }
}
