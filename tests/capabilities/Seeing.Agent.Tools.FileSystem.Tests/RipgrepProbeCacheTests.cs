using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Tools.FileSystem;
using System.ComponentModel;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// <see cref="RipgrepSearch"/> 探测缓存的失效语义。
/// <para>
/// 回归：原实现把「探测失败」永久缓存（<c>s_probed=true</c> 后永不重试）。
/// 用户在宿主进程启动后安装 rg，工具会一直报「ripgrep 不可用」直到重启进程。
/// 正确行为：成功结果长期缓存；失败结果只在有限窗口内缓存，过期后重探。
/// </para>
/// <para>
/// 时间经 <see cref="TimeProvider"/> 注入并手动推进，不使用真实等待。
/// </para>
/// </summary>
[Collection(RipgrepStaticStateCollection.Name)]
public class RipgrepProbeCacheTests
{
    public RipgrepProbeCacheTests() => RipgrepSearch.ResetProbeForTests();
    [Fact]
    public async Task Probe_WhenRgMissing_ShouldRetryAfterFailureCacheExpires()
    {
        // 首次探测：rg 不存在
        var starts = 0;
        var subprocess = CreateSubprocess(() =>
        {
            starts++;
            throw new Win32Exception("rg not found");
        });

        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        RipgrepSearch.TimeProviderForTests = clock;

        (await RipgrepSearch.IsRipgrepAvailableAsync(subprocess, default, NullLogger.Instance))
            .Should().BeFalse();
        starts.Should().Be(1);

        // 失败窗口内重复调用：命中缓存，不重探
        (await RipgrepSearch.IsRipgrepAvailableAsync(subprocess, default, NullLogger.Instance))
            .Should().BeFalse();
        starts.Should().Be(1);

        // 用户安装了 rg：窗口过期后必须重探并转为可用
        var rgInstalled = false;
        var installedSubprocess = CreateSubprocess(() =>
        {
            starts++;
            return rgInstalled
                ? new FakeSubprocess("ripgrep 13.0.0\n", 0)
                : throw new Win32Exception("rg not found");
        });
        rgInstalled = true;

        clock.Advance(RipgrepSearch.FailureCacheDuration + TimeSpan.FromSeconds(1));

        (await RipgrepSearch.IsRipgrepAvailableAsync(installedSubprocess, default, NullLogger.Instance))
            .Should().BeTrue("失败缓存过期后应重探，安装 rg 无需重启进程");
        starts.Should().Be(2);
    }

    [Fact]
    public async Task Probe_WhenRgAvailable_ShouldCacheSuccessIndefinitely()
    {
        var starts = 0;
        var subprocess = CreateSubprocess(() =>
        {
            starts++;
            return new FakeSubprocess("ripgrep 13\n", 0);
        });

        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        RipgrepSearch.TimeProviderForTests = clock;

        (await RipgrepSearch.IsRipgrepAvailableAsync(subprocess, default, NullLogger.Instance))
            .Should().BeTrue();

        // 成功结果不随长时间流逝重探（避免每次搜索多一次子进程开销）
        clock.Advance(TimeSpan.FromDays(30));

        (await RipgrepSearch.IsRipgrepAvailableAsync(subprocess, default, NullLogger.Instance))
            .Should().BeTrue();
        starts.Should().Be(1);
    }

    [Fact]
    public async Task Probe_WhenRgNotFoundInFailureCache_ShouldStillReportFallbackReason()
    {
        // 缓存命中时也必须给出可读原因，不能因为"没重探"就丢诊断
        var subprocess = CreateSubprocess(() => throw new Win32Exception("rg not found"));
        RipgrepSearch.TimeProviderForTests = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

        await RipgrepSearch.IsRipgrepAvailableAsync(subprocess, default, NullLogger.Instance);
        var available = await RipgrepSearch.IsRipgrepAvailableAsync(subprocess, default, NullLogger.Instance);

        available.Should().BeFalse();
    }

    [Fact]
    public async Task FallbackReason_WithinFailureCache_ShouldMentionRetryCountdown()
    {
        // 用户装了 rg 但缓存未过期时，文案必须说清"稍后会重试"，
        // 否则会被误读成"这台机器根本没装 ripgrep"（实际发生过的误判）。
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        RipgrepSearch.TimeProviderForTests = clock;

        var subprocess = CreateSubprocess(() => throw new Win32Exception("rg not found"));
        var world = CreateWorld(subprocess, fsExists: true);

        var outcome = await RipgrepSearch.GlobAsync(
            world, "/repo", "*.md", 100, default, NullLogger.Instance);

        outcome.UsedManaged.Should().BeTrue();
        outcome.Fallback.Should().NotBeNull();
        outcome.Fallback!.Reason.Should().Contain("后重试");
    }

    private static ISubprocessFactory CreateSubprocess(Func<ISubprocess> onStart)
    {
        var factory = new Moq.Mock<ISubprocessFactory>();
        factory.Setup(f => f.Start(Moq.It.IsAny<SubprocessSpec>()))
            .Returns(() => onStart());
        return factory.Object;
    }

    private static IExecutionWorld CreateWorld(ISubprocessFactory subprocess, bool fsExists)
    {
        var fs = new Moq.Mock<IFileSystem>();
        fs.Setup(f => f.Exists(Moq.It.IsAny<string>())).Returns(fsExists);
        fs.Setup(f => f.GetFullPath(Moq.It.IsAny<string>())).Returns((string p) => p);
        fs.Setup(f => f.EnumerateFiles(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<bool>()))
            .Returns(Array.Empty<string>());
        fs.Setup(f => f.EnumerateDirectories(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<bool>()))
            .Returns(Array.Empty<string>());
        fs.Setup(f => f.IsHidden(Moq.It.IsAny<string>())).Returns(false);
        fs.Setup(f => f.GetLastWriteTimeUtc(Moq.It.IsAny<string>())).Returns(DateTime.UtcNow);

        var world = new Moq.Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns("/repo");
        world.Setup(w => w.FileSystem).Returns(fs.Object);
        world.Setup(w => w.Subprocess).Returns(subprocess);
        return world.Object;
    }

    private sealed class FakeSubprocess : ISubprocess
    {
        public FakeSubprocess(string stdout, int exitCode)
        {
            StandardOutput = new StringReader(stdout);
            StandardError = new StringReader("");
            ExitCode = exitCode;
        }

        public int Id => 1;
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }
        public bool HasExited => true;
        public int ExitCode { get; }
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Kill(bool entireTree = true) { }
        public void Dispose() { }
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
