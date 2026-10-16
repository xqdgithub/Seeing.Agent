using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.FileSystem;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

public class OpenFileToolTests
{
    private static OpenFileTool CreateTool() =>
        new(NullLogger<OpenFileTool>.Instance, new Seeing.IO.Local.LocalFileSystem(), AllowAllPathGate.Instance);

    [Fact]
    public async Task ExecuteAsync_ValidPath_ShouldSucceedAndWriteMetadata()
    {
        var file = Path.Combine(Path.GetTempPath(), "seeing_open_" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "x", TestContext.Current.CancellationToken);

        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = file }), new ToolContext());

        result.Success.Should().BeTrue();
        result.Metadata.Should().ContainKey("path");
        result.Metadata["path"].Should().Be(Path.GetFullPath(file));
    }

    [Fact]
    public async Task ExecuteAsync_WithLineColumn_ShouldWriteMetadata()
    {
        var file = Path.Combine(Path.GetTempPath(), "seeing_open_" + Guid.NewGuid().ToString("N") + ".txt");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = file, line = 12, column = 4 }), new ToolContext());

        result.Success.Should().BeTrue();
        result.Metadata["line"].Should().Be(12);
        result.Metadata["column"].Should().Be(4);
    }

    [Fact]
    public async Task ExecuteAsync_MissingPath_ShouldFail()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { }), new ToolContext());

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHaveNoDiskSideEffect()
    {
        // 打开不存在的路径也应成功（仅请求前端打开，不读盘）
        var file = Path.Combine(Path.GetTempPath(), "seeing_open_ghost_" + Guid.NewGuid().ToString("N") + ".txt");

        var tool = CreateTool();
        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = file }), new ToolContext());

        result.Success.Should().BeTrue();
        File.Exists(file).Should().BeFalse();
    }
}
