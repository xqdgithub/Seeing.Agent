using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.FileSystem;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

[Collection(RipgrepStaticStateCollection.Name)]
public class GrepGlobSubprocessTests
{
    public GrepGlobSubprocessTests()
    {
        RipgrepSearch.ResetProbeForTests();
    }

    [Fact]
    public async Task Grep_WhenRgAvailable_ShouldSpawnViaISubprocess()
    {
        SubprocessSpec? lastSpec = null;
        var world = CreateWorld(
            onStart: spec =>
            {
                lastSpec = spec;
                if (spec.FileName == "rg" && spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14.0.0\n", exitCode: 0);

                // rg --json 结构化输出：path.text 为 Windows 绝对路径（盘符冒号不再参与文本解析）
                var matchJson =
                    """{"type":"match","data":{"path":{"text":"C:\\repo\\a.cs"},"lines":{"text":"hello match\n"},"line_number":10}}""";
                return new FakeSubprocess(matchJson + "\n", exitCode: 0);
            },
            fsExists: true);

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "hello", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        lastSpec!.FileName.Should().Be("rg");
        lastSpec.ArgumentList.Should().Contain("--json");
        lastSpec.ArgumentList.Should().Contain("hello");
        result.Output.Should().Contain("hello match");
        result.Output.Should().Contain(@"C:\repo\a.cs:10:hello match");
        result.Metadata["backend"].Should().Be("rg");
    }

    [Fact]
    public async Task Grep_Arguments_ShouldNeverContainQuoteCharacters()
    {
        // 回归：原实现用 Quote() 拼命令行字符串，POSIX 分支生成单引号，
        // 而 .NET 的切分器不把单引号当引号 → rg 收到带字面引号的模式，永远 exit=1。
        SubprocessSpec? searchSpec = null;
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);
                searchSpec = spec;
                return new FakeSubprocess("", exitCode: 1);
            },
            fsExists: true);

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "招考 简章", path = @"/repo with space", include = "*.md" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        searchSpec!.ArgumentList.Should().NotBeNull();
        searchSpec.Arguments.Should().BeEmpty();
        searchSpec.ArgumentList.Should().Contain("招考 简章");
        searchSpec.ArgumentList.Should().Contain("/repo with space");
        searchSpec.ArgumentList.Should().NotContain(a => a.Contains('\'') || a.Contains('"'));
    }

    [Fact]
    public async Task Grep_WhenRgExitOne_ShouldNotFallbackToManaged()
    {
        // exit=1 是「无匹配」的可信答案，不得回退（回退会掩盖参数错误）。
        var fsEnumerated = false;
        var world = CreateWorld(
            onStart: spec => spec.ArgumentList?.Contains("--version") == true
                ? new FakeSubprocess("ripgrep 14\n", 0)
                : new FakeSubprocess("", exitCode: 1),
            fsExists: true,
            onEnumerate: () => fsEnumerated = true);

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "needle", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("未找到匹配的文件");
        result.Metadata["backend"].Should().Be("rg");
        fsEnumerated.Should().BeFalse("exit=1 不得回退托管");
    }

    [Fact]
    public async Task Grep_WhenRgWritesLargeStderr_ShouldDrainWithoutDeadlock()
    {
        // rg 输出大量 stderr：若只读 stdout，stderr 管道会填满导致子进程阻塞/死锁。
        var stderr = new TrackingTextReader(new string('x', 200_000));
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);

                var matchJson =
                    """{"type":"match","data":{"path":{"text":"C:\\repo\\a.cs"},"lines":{"text":"hit\n"},"line_number":1}}""";
                return new FakeSubprocess(matchJson + "\n", exitCode: 0, stderr: stderr);
            },
            fsExists: true);

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "hit", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        stderr.WasRead.Should().BeTrue("stderr 必须被并发排空以释放管道");
    }

    [Fact]
    public async Task Grep_WhenRgFailsWithStderr_ShouldLogStderrAndFallback()
    {
        var logger = new CapturingLogger();
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);

                // 退出码 2 = rg 错误，同时带 stderr 诊断信息
                return new FakeSubprocess("", exitCode: 2, stderr: new TrackingTextReader("permission denied: boom"));
            },
            fsExists: true,
            enumerateFiles: [@"C:\repo\a.cs"],
            fileContent: "needle here\n");

        var tool = new GrepTool(logger, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "needle", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("needle");
        result.Output.Should().Contain("已回退托管搜索");
        result.Output.Should().Contain("boom");
        result.Metadata["backend"].Should().Be("managed");
        logger.Messages.Should().Contain(m => m.Contains("boom"));
    }

    [Fact]
    public async Task Grep_WhenRgReturnsPathBytes_ShouldDecodeBase64()
    {
        // 非 UTF-8 文件名在 rg --json 中走 path.bytes（base64），原实现会静默丢弃。
        var pathBytes = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(@"C:\repo\中文.cs"));
        var matchJson =
            """{"type":"match","data":{"path":{"bytes":"__BYTES__"},"lines":{"text":"hit\n"},"line_number":3}}"""
                .Replace("__BYTES__", pathBytes);

        var world = CreateWorld(
            onStart: spec => spec.ArgumentList?.Contains("--version") == true
                ? new FakeSubprocess("ripgrep 14\n", 0)
                : new FakeSubprocess(matchJson + "\n", exitCode: 0),
            fsExists: true);

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "hit", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Output.Should().Contain("中文.cs");
        result.Output.Should().Contain(":3:");
    }

    [Fact]
    public async Task Grep_WhenRgMissing_ShouldFallbackToManaged()
    {
        var world = CreateWorld(
            onStart: _ => throw new System.ComponentModel.Win32Exception("rg not found"),
            fsExists: true,
            enumerateFiles: [@"C:\repo\a.cs"],
            fileContent: "line with needle here\n");

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "needle", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("needle");
    }

    [Fact]
    public async Task Glob_WhenRgAvailable_ShouldUseFilesFlag()
    {
        SubprocessSpec? searchSpec = null;
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);
                searchSpec = spec;
                return new FakeSubprocess("src/a.cs\nsrc/b.cs\n", 0);
            },
            fsExists: true);

        var tool = new GlobTool(NullLogger<GlobTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "**/*.cs", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        searchSpec!.ArgumentList.Should().Contain("--files");
        result.Output.Should().Contain("src/a.cs");
        result.Metadata["backend"].Should().Be("rg");
    }

    [Fact]
    public async Task Glob_WhenHiddenTrue_ShouldPassHiddenFlag()
    {
        SubprocessSpec? searchSpec = null;
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);
                searchSpec = spec;
                return new FakeSubprocess(".github/ci.yml\n", 0);
            },
            fsExists: true);

        var tool = new GlobTool(NullLogger<GlobTool>.Instance, world, AllowAllPathGate.Instance);
        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "*.yml", path = @"C:\repo", hidden = true }),
            new ToolContext { SessionId = "s", CallId = "c" });

        searchSpec!.ArgumentList.Should().Contain("--hidden");
    }

    [Fact]
    public async Task Glob_WhenHiddenFalse_ShouldNotPassHiddenFlag()
    {
        SubprocessSpec? searchSpec = null;
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);
                searchSpec = spec;
                return new FakeSubprocess("a.cs\n", 0);
            },
            fsExists: true);

        var tool = new GlobTool(NullLogger<GlobTool>.Instance, world, AllowAllPathGate.Instance);
        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "*.cs", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        searchSpec!.ArgumentList.Should().NotContain("--hidden");
    }

    [Fact]
    public async Task Grep_WhenHiddenTrue_ShouldPassHiddenFlag()
    {
        SubprocessSpec? searchSpec = null;
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);
                searchSpec = spec;
                return new FakeSubprocess("", exitCode: 1);
            },
            fsExists: true);

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "x", path = @"C:\repo", hidden = true }),
            new ToolContext { SessionId = "s", CallId = "c" });

        searchSpec!.ArgumentList.Should().Contain("--hidden");
    }

    [Fact]
    public async Task Glob_ShouldNotPassMaxCountToFiles()    {
        // 回归：--files 不接受 --max-count（实测传了被忽略），全局截断只能在 C# 侧做。
        SubprocessSpec? searchSpec = null;
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.ArgumentList?.Contains("--version") == true)
                    return new FakeSubprocess("ripgrep 14\n", 0);
                searchSpec = spec;
                return new FakeSubprocess("a.cs\n", 0);
            },
            fsExists: true);

        var tool = new GlobTool(NullLogger<GlobTool>.Instance, world, AllowAllPathGate.Instance);
        await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "*.cs", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        searchSpec!.ArgumentList.Should().NotContain("--max-count");
        searchSpec.ArgumentList.Should().Contain("--sortr=modified");
    }

    [Fact]
    public async Task Glob_ShouldDetectTruncationOnlyWhenMoreThanLimit()
    {
        // 恰好 limit 条不算截断（原实现 count >= limit 会误报）
        var exactly = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"f{i}.cs")) + "\n";
        var world = CreateWorld(
            onStart: spec => spec.ArgumentList?.Contains("--version") == true
                ? new FakeSubprocess("ripgrep 14\n", 0)
                : new FakeSubprocess(exactly, 0),
            fsExists: true);

        var tool = new GlobTool(NullLogger<GlobTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "*.cs", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Metadata["truncated"].Should().Be(false);
    }

    [Fact]
    public async Task Glob_WhenRgExitOne_ShouldReportNoMatchWithoutFallback()
    {
        var fsEnumerated = false;
        var world = CreateWorld(
            onStart: spec => spec.ArgumentList?.Contains("--version") == true
                ? new FakeSubprocess("ripgrep 14\n", 0)
                : new FakeSubprocess("", exitCode: 1),
            fsExists: true,
            onEnumerate: () => fsEnumerated = true);

        var tool = new GlobTool(NullLogger<GlobTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "*.cs", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("未找到匹配文件");
        result.Metadata["backend"].Should().Be("rg");
        fsEnumerated.Should().BeFalse();
    }

    [Fact]
    public void GlobSearch_WhenCancelledDuringTraversal_ShouldThrowOperationCanceled()
    {
        // 回归：托管遍历曾完全忽略取消令牌，导致 glob 在大目录上不可中断、取消后会话挂死。
        using var cts = new CancellationTokenSource();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.EnumerateFiles(It.IsAny<string>(), "*", It.IsAny<bool>()))
            .Returns(() =>
            {
                cts.Cancel(); // 遍历开始后取消（模拟用户点取消）
                return new[] { @"C:\repo\a.cs" };
            });
        fs.Setup(f => f.EnumerateDirectories(It.IsAny<string>(), "*", It.IsAny<bool>()))
            .Returns(Array.Empty<string>());
        fs.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<string>())).Returns(DateTime.UtcNow);

        var act = () => FileSystemHelper.GlobSearch(fs.Object, @"C:\repo", "**/*.cs", 100, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void GrepSearch_WhenCancelledDuringTraversal_ShouldThrowOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.EnumerateFiles(It.IsAny<string>(), "*", It.IsAny<bool>()))
            .Returns(() =>
            {
                cts.Cancel();
                return new[] { @"C:\repo\a.cs" };
            });
        fs.Setup(f => f.EnumerateDirectories(It.IsAny<string>(), "*", It.IsAny<bool>()))
            .Returns(Array.Empty<string>());

        var act = () => FileSystemHelper.GrepSearch(fs.Object, @"C:\repo", "needle", null, 100, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task Glob_WhenCancelledDuringManagedFallback_ShouldThrowOperationCanceled()
    {
        // 工具级：rg 不可用 → 托管回退期间取消必须向上穿透为 OCE（执行器据此标记「已取消」）。
        using var cts = new CancellationTokenSource();
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.Exists(It.IsAny<string>())).Returns(true);
        fs.Setup(f => f.GetFullPath(It.IsAny<string>())).Returns((string p) => p);
        fs.Setup(f => f.EnumerateFiles(It.IsAny<string>(), "*", It.IsAny<bool>()))
            .Returns(() =>
            {
                cts.Cancel();
                return new[] { @"C:\repo\a.cs" };
            });
        fs.Setup(f => f.EnumerateDirectories(It.IsAny<string>(), "*", It.IsAny<bool>()))
            .Returns(Array.Empty<string>());
        fs.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<string>())).Returns(DateTime.UtcNow);

        var subprocess = new Mock<ISubprocessFactory>();
        subprocess.Setup(s => s.Start(It.IsAny<SubprocessSpec>()))
            .Throws(new System.ComponentModel.Win32Exception("rg not found"));

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(@"C:\repo");
        world.Setup(w => w.FileSystem).Returns(fs.Object);
        world.Setup(w => w.Subprocess).Returns(subprocess.Object);

        var tool = new GlobTool(NullLogger<GlobTool>.Instance, world.Object, AllowAllPathGate.Instance);

        var act = async () => await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "**/*.cs", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c", CancellationToken = cts.Token });

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static IExecutionWorld CreateWorld(
        Func<SubprocessSpec, ISubprocess> onStart,
        bool fsExists,
        IReadOnlyList<string>? enumerateFiles = null,
        string? fileContent = null,
        Action? onEnumerate = null)
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.Exists(It.IsAny<string>())).Returns(fsExists);
        fs.Setup(f => f.GetFullPath(It.IsAny<string>())).Returns((string p) => p);
        fs.Setup(f => f.EnumerateFiles(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(() =>
            {
                onEnumerate?.Invoke();
                return enumerateFiles ?? Array.Empty<string>();
            });
        fs.Setup(f => f.EnumerateDirectories(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(() =>
            {
                onEnumerate?.Invoke();
                return Array.Empty<string>();
            });
        fs.Setup(f => f.GetLastWriteTimeUtc(It.IsAny<string>())).Returns(DateTime.UtcNow);
        if (fileContent != null)
        {
            fs.Setup(f => f.OpenRead(It.IsAny<string>()))
                .Returns(() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fileContent)));
        }

        var subprocess = new Mock<ISubprocessFactory>();
        subprocess.Setup(s => s.Start(It.IsAny<SubprocessSpec>()))
            .Returns((SubprocessSpec spec) => onStart(spec));

        var world = new Mock<IExecutionWorld>();
        world.Setup(w => w.Cwd).Returns(@"C:\repo");
        world.Setup(w => w.FileSystem).Returns(fs.Object);
        world.Setup(w => w.Subprocess).Returns(subprocess.Object);
        return world.Object;
    }

    private sealed class FakeSubprocess : ISubprocess
    {
        public FakeSubprocess(string stdout, int exitCode = 0, TextReader? stderr = null)
        {
            StandardOutput = new StringReader(stdout);
            StandardError = stderr ?? new StringReader("");
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

    /// <summary>记录是否被读取过的 stderr 读取器，用于断言排空行为。</summary>
    private sealed class TrackingTextReader : TextReader
    {
        private readonly StringReader _inner;
        private int _read;

        public TrackingTextReader(string content) => _inner = new StringReader(content);

        public bool WasRead => Volatile.Read(ref _read) != 0;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _read, 1);
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _read, 1);
            return _inner.ReadToEndAsync(cancellationToken);
        }

        public override string ReadToEnd()
        {
            Interlocked.Exchange(ref _read, 1);
            return _inner.ReadToEnd();
        }

        public override int Read(char[] buffer, int index, int count)
        {
            Interlocked.Exchange(ref _read, 1);
            return _inner.Read(buffer, index, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>捕获日志消息的 ILogger，用于断言 stderr 诊断。</summary>
    private sealed class CapturingLogger : ILogger<GrepTool>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
