using FluentAssertions;
using Seeing.Agent.Tools.Browser;
using Xunit;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 发现文件、宿主单例锁、客户端存活锁与页面归属清单的测试。
/// </summary>
/// <remarks>
/// 客户端锁 / 页面清单通过 internal 重载显式指定临时根目录，不写真实
/// <c>~/.seeing/browser/clients</c>；每个用例用独立目录，<see cref="System.IDisposable"/> 清理。
/// </remarks>
public sealed class BrowserHostLocatorTests
{
    /// <summary>用例内隔离的客户端锁根目录（与生产默认根目录同构，但不污染用户目录）。</summary>
    private static string Root(string profileDirectory) => Path.Combine(profileDirectory, "browser-root");

    [Fact]
    public void HostLock_WhenHeld_SecondAcquireFails()
    {
        using var dir = new TempDirectory();

        using var first = BrowserHostLock.TryAcquire(dir.Path);

        first.Should().NotBeNull();
        BrowserHostLock.TryAcquire(dir.Path).Should().BeNull();
    }

    [Fact]
    public void HostLock_AfterRelease_CanBeAcquiredAgain()
    {
        using var dir = new TempDirectory();

        var first = BrowserHostLock.TryAcquire(dir.Path);
        first.Should().NotBeNull();
        first!.Dispose();

        using var second = BrowserHostLock.TryAcquire(dir.Path);
        second.Should().NotBeNull();
    }

    [Fact]
    public void HostLock_ShouldKeepLockFileAfterRelease()
    {
        using var dir = new TempDirectory();

        BrowserHostLock.TryAcquire(dir.Path)!.Dispose();

        File.Exists(Path.Combine(dir.Path, BrowserHostLock.FileName))
            .Should().BeTrue("锁文件必须永不删除，否则 unlink 竞态会让两个宿主同时拿到锁");
    }

    [Fact]
    public void ClientPresence_WhenDisposed_ShouldBeDetectedAsDead()
    {
        using var dir = new TempDirectory();
        var pid = Environment.ProcessId;

        var presence = BrowserClientPresence.Start(dir.Path, Root(dir.Path));
        BrowserClientPresence.AliveClientPids(dir.Path, Root(dir.Path)).Should().Contain(pid);

        presence.Dispose();

        BrowserClientPresence.AliveClientPids(dir.Path, Root(dir.Path)).Should().NotContain(pid);
    }

    [Fact]
    public void ClientPresence_AcquirePage_ShouldCreateBothLocks()
    {
        using var dir = new TempDirectory();
        var pid = Environment.ProcessId;

        using var presence = BrowserClientPresence.Start(dir.Path, Root(dir.Path));
        presence.AcquirePage("target-1");

        File.Exists(presence.LockPath).Should().BeTrue();
        File.Exists(presence.PagesPath).Should().BeTrue();
        BrowserClientPresence.AliveClientPids(dir.Path, Root(dir.Path)).Should().Contain(pid);
    }

    [Fact]
    public void ClientPresence_ReleaseAllPages_ThenAcquireAgain_ShouldRegainLock()
    {
        using var dir = new TempDirectory();
        var pid = Environment.ProcessId;

        using var presence = BrowserClientPresence.Start(dir.Path, Root(dir.Path));

        presence.AcquirePage("target-1");
        presence.ReleasePage("target-1");
        BrowserClientPresence.AliveClientPids(dir.Path, Root(dir.Path)).Should().NotContain(pid);

        // 最后一个页面关闭后锁被释放；再次开页必须重新加锁（presence 对象随进程存活）
        presence.AcquirePage("target-2");
        BrowserClientPresence.AliveClientPids(dir.Path, Root(dir.Path)).Should().Contain(pid);

        presence.ReleasePage("target-2");
    }

    [Fact]
    public void ClientPresence_PageManifest_ShouldTrackTargetIds()
    {
        using var dir = new TempDirectory();
        var pid = Environment.ProcessId;

        using var presence = BrowserClientPresence.Start(dir.Path, Root(dir.Path));
        presence.AcquirePage("target-b");
        presence.AcquirePage("target-a");

        BrowserClientPresence.ReadPageTargets(dir.Path, Root(dir.Path), pid)
            .Should().BeEquivalentTo(["target-a", "target-b"]);

        presence.ReleasePage("target-a");

        BrowserClientPresence.ReadPageTargets(dir.Path, Root(dir.Path), pid)
            .Should().BeEquivalentTo(["target-b"]);
    }

    [Fact]
    public void ClientPresence_AcquireSamePageTwice_ShouldBeIdempotent()
    {
        using var dir = new TempDirectory();
        var pid = Environment.ProcessId;

        using var presence = BrowserClientPresence.Start(dir.Path, Root(dir.Path));
        presence.AcquirePage("target-1");
        presence.AcquirePage("target-1");

        presence.ReleasePage("target-1");

        // 重复 acquire 不应把计数加两次；释放一次就该回到 0 页
        BrowserClientPresence.AliveClientPids(dir.Path, Root(dir.Path)).Should().NotContain(pid);
    }

    [Fact]
    public void InfoPath_ShouldBeStablePerProfile()
        => BrowserHostLocator.ComputeInfoPath(@"C:\p\A", @"C:\base")
            .Should().Be(BrowserHostLocator.ComputeInfoPath(@"C:\p\A", @"C:\base"));

    [Fact]
    public void InfoPath_ShouldDifferPerProfile()
        => BrowserHostLocator.ComputeInfoPath(@"C:\p\A", @"C:\base")
            .Should().NotBe(BrowserHostLocator.ComputeInfoPath(@"C:\p\B", @"C:\base"));

    [Fact]
    public void ProfileHash_ShouldIgnoreTrailingSeparator()
        => BrowserHostLocator.ComputeProfileHash(@"C:\p\A\")
            .Should().Be(BrowserHostLocator.ComputeProfileHash(@"C:\p\A"));

    [Fact]
    public void ResolveHostProfileDirectory_WhenConfigured_ShouldWin()
    {
        var configured = Path.Combine(Path.GetTempPath(), "seeing-configured-profile");

        BrowserHostLocator.ResolveHostProfileDirectory(configured, Path.GetTempPath())
            .Should().Be(Path.GetFullPath(configured));
    }

    [Fact]
    public void ResolveHostProfileDirectory_WhenNotConfigured_ShouldBeStableDefault()
    {
        var home = Path.Combine(Path.GetTempPath(), "seeing-home");
        var expected = Path.Combine(home, ".seeing", BrowserHostLocator.ProfileDirectoryName);

        BrowserHostLocator.ResolveHostProfileDirectory(null, home).Should().Be(expected);

        // 空白配置等同于未配置：默认路径必须稳定（宿主单例键与发现文件 hash 都来自它）
        BrowserHostLocator.ResolveHostProfileDirectory("   ", home).Should().Be(expected);
    }

    [Fact]
    public void Info_WhenContentIsCorrupt_ShouldBeNull()
    {
        using var dir = new TempDirectory();
        var locator = new BrowserHostLocator(dir.Path, Root(dir.Path));

        Directory.CreateDirectory(Path.GetDirectoryName(locator.InfoPath)!);
        File.WriteAllText(locator.InfoPath, "{ not json");

        // 半截 / 改坏的文件必须降级成「没有宿主」而不是抛（但仍记 Debug，见类型注释）
        locator.Read().Should().BeNull();
    }

    [Fact]
    public void Info_RoundTrip_ShouldPreserveAllFields()
    {
        using var dir = new TempDirectory();
        var locator = new BrowserHostLocator(dir.Path, Path.Combine(dir.Path, "base"));
        var startedAt = new DateTimeOffset(2026, 11, 19, 10, 23, 45, TimeSpan.FromHours(8));
        var info = new BrowserHostInfo(
            BrowserHostLocator.CurrentVersion,
            "8f3c1e2a",
            12345,
            startedAt,
            dir.Path,
            BrowserPid: 31420,
            BrowserStartedAt: startedAt.AddSeconds(1),
            Endpoint: "ws://127.0.0.1:52341/devtools/browser/abc",
            LastError: null,
            Effective: new BrowserHostEffective(
                Headless: true,
                ExecutablePath: @"C:\Edge\msedge.exe",
                UserAgent: null,
                AntiDetection: true,
                ExtraArgs: ["--lang=zh-CN"],
                HostIdleSeconds: 1800));

        locator.Write(info);

        // 记录含数组成员（ExtraArgs），默认相等性是引用比较，这里按成员逐一比对
        locator.Read().Should().BeEquivalentTo(info);
        locator.VersionMatches(locator.Read()).Should().BeTrue();
    }

    [Fact]
    public void Info_WhenFileMissing_ShouldBeNull()
    {
        using var dir = new TempDirectory();
        var locator = new BrowserHostLocator(dir.Path, Path.Combine(dir.Path, "base"));

        locator.Read().Should().BeNull();
        locator.VersionMatches(null).Should().BeFalse();
    }

    [Fact]
    public void Info_WhenVersionDiffers_ShouldNotMatch()
    {
        using var dir = new TempDirectory();
        var locator = new BrowserHostLocator(dir.Path, Path.Combine(dir.Path, "base"));
        locator.Write(NewInfo(dir.Path, hostPid: Environment.ProcessId) with
        {
            Version = BrowserHostLocator.CurrentVersion + 1
        });

        locator.VersionMatches(locator.Read()).Should().BeFalse();
    }

    [Fact]
    public void TryDelete_WhenHostPidIsAnotherProcess_ShouldKeepFile()
    {
        using var dir = new TempDirectory();
        var locator = new BrowserHostLocator(dir.Path, Path.Combine(dir.Path, "base"));
        locator.Write(NewInfo(dir.Path, hostPid: Environment.ProcessId + 1));

        locator.TryDelete().Should().BeFalse();
        locator.Read().Should().NotBeNull();
    }

    [Fact]
    public void TryDelete_WhenHostPidIsCurrentProcess_ShouldRemoveFile()
    {
        using var dir = new TempDirectory();
        var locator = new BrowserHostLocator(dir.Path, Path.Combine(dir.Path, "base"));
        locator.Write(NewInfo(dir.Path, hostPid: Environment.ProcessId));

        locator.TryDelete().Should().BeTrue();
        locator.Read().Should().BeNull();
    }

    [Fact]
    public void IsSameHost_WhenStartedAtDiffers_ShouldBeFalse()
    {
        var info = new BrowserHostInfo(1, "g", 123, DateTimeOffset.UtcNow, @"C:\p",
            null, null, "ws://x", null, null);

        BrowserHostLocator.IsSameHost(info, 123, info.HostStartedAt.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void IsSameHost_WhenPidDiffers_ShouldBeFalse()
    {
        var info = new BrowserHostInfo(1, "g", 123, DateTimeOffset.UtcNow, @"C:\p",
            null, null, "ws://x", null, null);

        BrowserHostLocator.IsSameHost(info, 124, info.HostStartedAt).Should().BeFalse();
    }

    [Fact]
    public void IsSameHost_WhenPidAndStartedAtMatch_ShouldBeTrue()
    {
        var info = new BrowserHostInfo(1, "g", 123, DateTimeOffset.UtcNow, @"C:\p",
            null, null, "ws://x", null, null);

        BrowserHostLocator.IsSameHost(info, 123, info.HostStartedAt).Should().BeTrue();
    }

    private static BrowserHostInfo NewInfo(string profileDirectory, int hostPid)
        => new(
            BrowserHostLocator.CurrentVersion,
            "generation",
            hostPid,
            DateTimeOffset.UtcNow,
            profileDirectory,
            BrowserPid: null,
            BrowserStartedAt: null,
            Endpoint: "ws://127.0.0.1:1/devtools/browser/x",
            LastError: null,
            Effective: null);

    /// <summary>
    /// 用例级临时目录：profile 目录身份的唯一来源，用例结束尽力删除。
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "seeing-browser-host-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 用例结束清理失败不影响断言结果
            }
        }
    }
}
