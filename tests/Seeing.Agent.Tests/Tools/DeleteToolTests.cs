using Seeing.Agent.Abstractions.Tools;
// tests/Seeing.Agent.Tests/Tools/DeleteToolTests.cs

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Tools.BuiltIn.FileSystem;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class DeleteToolTests
{
    private static DeleteTool CreateTool() => new(NullLogger<DeleteTool>.Instance);

    [Fact]
    public async Task ExecuteAsync_DeleteFile_ShouldSucceed()
    {
        var file = Path.Combine(Path.GetTempPath(), "seeing_del_" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "x");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = file }), new ToolContext());

        result.Success.Should().BeTrue();
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_DeleteDirectoryRecursive_ShouldSucceed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "seeing_del_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dir, "sub", "a.txt"), "x");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = dir }), new ToolContext());

        result.Success.Should().BeTrue();
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_NonexistentPath_ShouldFail()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = Path.Combine(Path.GetTempPath(), "no_such_" + Guid.NewGuid().ToString("N")) }),
            new ToolContext());

        result.Success.Should().BeFalse();
    }
}
