using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Management;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Management;

public class SessionManagerClearCacheTests
{
    [Fact]
    public async Task ClearCache_清空内存缓存()
    {
        var store = new InMemorySessionStore();
        var manager = new SessionManager(store: store);

        // 仅注册到内存缓存（不落存储），模拟工作区切换前遗留的陈旧缓存
        manager.Register(new SessionData { Id = "s1", Title = "旧会话" });
        manager.Get("s1").Should().NotBeNull();

        manager.ClearCache();

        manager.Get("s1").Should().BeNull();
        (await manager.LoadAsync("s1")).Should().BeNull(); // 内存缓存已清，存储无此会话
    }
}