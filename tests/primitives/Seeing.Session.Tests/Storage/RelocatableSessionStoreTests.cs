using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class RelocatableSessionStoreTests
{
    [Fact]
    public async Task SetBaseDirectory_切换后读写新目录()
    {
        var root1 = Path.Combine(Path.GetTempPath(), "seeing-session-test-" + Guid.NewGuid().ToString("N"));
        var root2 = Path.Combine(Path.GetTempPath(), "seeing-session-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(root1);
            var session = new SessionData { Id = "s1", Title = "旧工作区会话" };
            await store.SaveAsync(session);

            store.SetBaseDirectory(root2);
            await store.SaveAsync(new SessionData { Id = "s2", Title = "新工作区会话" });

            (await store.LoadAsync("s1")).Should().BeNull();   // 旧目录不可见
            (await store.LoadAsync("s2")).Should().NotBeNull(); // 新目录可读写
        }
        finally
        {
            if (Directory.Exists(root1)) Directory.Delete(root1, true);
            if (Directory.Exists(root2)) Directory.Delete(root2, true);
        }
    }
}
