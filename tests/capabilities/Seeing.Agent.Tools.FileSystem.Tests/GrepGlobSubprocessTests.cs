using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.FileSystem;
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
                return new FakeSubprocess("src/a.cs:10:hello match\n", exitCode: 0);
            },
            fsExists: true);

        var tool = new GrepTool(NullLogger<GrepTool>.Instance, world, AllowAllPathGate.Instance);
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { pattern = "hello", path = @"C:\repo" }),
            new ToolContext { SessionId = "s", CallId = "c" });

        result.Success.Should().BeTrue();
        lastSpec!.FileName.Should().Be("rg");
        lastSpec.Arguments.Should().Contain("hello");
        result.Output.Should().Contain("hello match");
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
        public FakeSubprocess(string stdout, int exitCode = 0)
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
}
