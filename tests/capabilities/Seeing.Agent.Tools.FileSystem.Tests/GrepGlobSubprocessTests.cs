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
                if (spec.FileName == "rg" && spec.Arguments.Contains("--version"))
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
        lastSpec.Arguments.Should().Contain("--json");
        lastSpec.Arguments.Should().Contain("hello");
        result.Output.Should().Contain("hello match");
        result.Output.Should().Contain(@"C:\repo\a.cs:10:hello match");
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
                if (spec.FileName == "rg" && spec.Arguments.Contains("--version"))
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
        searchSpec!.Arguments.Should().Contain("--files");
        result.Output.Should().Contain("src/a.cs");
    }

    [Fact]
    public async Task Grep_WhenRgWritesLargeStderr_ShouldDrainWithoutDeadlock()
    {
        // rg 输出大量 stderr：若只读 stdout，stderr 管道会填满导致子进程阻塞/死锁。
        var stderr = new TrackingTextReader(new string('x', 200_000));
        var world = CreateWorld(
            onStart: spec =>
            {
                if (spec.FileName == "rg" && spec.Arguments.Contains("--version"))
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
                if (spec.FileName == "rg" && spec.Arguments.Contains("--version"))
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
        logger.Messages.Should().Contain(m => m.Contains("boom"));
    }

    private static IExecutionWorld CreateWorld(
        Func<SubprocessSpec, ISubprocess> onStart,
        bool fsExists,
        IReadOnlyList<string>? enumerateFiles = null,
        string? fileContent = null)
    {
        var fs = new Mock<IFileSystem>();
        fs.Setup(f => f.Exists(It.IsAny<string>())).Returns(fsExists);
        fs.Setup(f => f.GetFullPath(It.IsAny<string>())).Returns((string p) => p);
        fs.Setup(f => f.EnumerateFiles(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(enumerateFiles ?? Array.Empty<string>());
        fs.Setup(f => f.EnumerateDirectories(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(Array.Empty<string>());
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
