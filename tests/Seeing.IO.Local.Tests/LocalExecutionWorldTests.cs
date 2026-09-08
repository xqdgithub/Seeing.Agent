using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Execution;
using Xunit;

namespace Seeing.IO.Local.Tests;

public class LocalExecutionWorldTests
{
    [Fact]
    public void LocalFileSystem_ReadWrite_WorksInTempDirectory()
    {
        var fs = new LocalFileSystem();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var filePath = Path.Combine(tempDir, "test.txt");
            fs.WriteAllText(filePath, "hello");
            fs.Exists(filePath).Should().BeTrue();
            fs.ReadAllText(filePath).Should().Be("hello");
            fs.GetFullPath(filePath).Should().Be(Path.GetFullPath(filePath));
            fs.EnumerateFiles(tempDir, "*.txt", recursive: false).Should().Contain(filePath);
            fs.EnumerateDirectories(tempDir, "*", recursive: false).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalFileSystem_AsyncReadWrite_WorksInTempDirectory()
    {
        var fs = new LocalFileSystem();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var filePath = Path.Combine(tempDir, "async.txt");
            await fs.WriteAllTextAsync(filePath, "line1\nline2");
            (await fs.ReadAllTextAsync(filePath)).Should().Be("line1\nline2");

            var lines = new List<string>();
            await foreach (var line in fs.ReadLinesAsync(filePath))
            {
                lines.Add(line);
            }

            lines.Should().Equal("line1", "line2");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalSubprocessFactory_Start_CmdEcho_ReturnsOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var factory = new LocalSubprocessFactory();
        using var process = factory.Start(new SubprocessSpec
        {
            FileName = "cmd",
            Arguments = "/c echo hi",
        });

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        output.Trim().Should().Be("hi");
        process.HasExited.Should().BeTrue();
    }

    [Fact]
    public void LocalExecutionWorldModule_Id_ShouldBeIoLocal()
    {
        var module = new LocalExecutionWorldModule();

        module.Id.Should().Be("io.local");
        module.ProvidedSeams.Should().Equal("executionWorld");
        module.ProvidedTools.Should().BeEmpty();
        module.DependsOn.Should().BeEmpty();
    }

    [Fact]
    public void LocalExecutionWorldModule_ConfigureServices_RegistersExecutionWorldSingleton()
    {
        var services = new ServiceCollection();
        new LocalExecutionWorldModule().ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        var world = provider.GetRequiredService<IExecutionWorld>();

        world.Should().BeOfType<LocalExecutionWorld>();
        world.FileSystem.Should().BeOfType<LocalFileSystem>();
        world.Subprocess.Should().BeOfType<LocalSubprocessFactory>();
        world.Cwd.Should().Be(Directory.GetCurrentDirectory());
    }
}
