using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Integration.Tools;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Integration;

public class MemoryWriteToolTests
{
    [Fact]
    public void BuildDocument_ShouldUseMaxImportanceAndExplicitKind()
    {
        var doc = MemoryWriteTool.BuildDocument(
            "explicit-abc",
            "偏好",
            "喜欢简体中文",
            new[] { "pref", "ui" },
            "sess-1");

        doc.Should().Contain("importance: 1.0");
        doc.Should().Contain("kind: explicit");
        doc.Should().Contain("source: tool");
        doc.Should().Contain("source_session: sess-1");
        doc.Should().Contain("title: \"偏好\"");
        doc.Should().Contain("tags: [pref, ui]");
        doc.Should().Contain("喜欢简体中文");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSaveViaMemoryService()
    {
        string? savedPath = null;
        string? savedContent = null;

        var memory = new Mock<IMemoryService>();
        memory
            .Setup(m => m.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, string content, CancellationToken _) =>
            {
                savedPath = path;
                savedContent = content;
                return FileNode.Create(
                    path,
                    content,
                    FileMetadata.Create("id", MemoryType.Daily, "t") with { Importance = 1.0 });
            });

        var services = new ServiceCollection();
        services.AddScoped(_ => memory.Object);
        await using var sp = services.BuildServiceProvider();

        var tool = new MemoryWriteTool(NullLogger<MemoryWriteTool>.Instance, sp.GetRequiredService<IServiceScopeFactory>());
        var args = JsonSerializer.SerializeToElement(new
        {
            title = "用户偏好",
            content = "默认使用暗色主题",
            tags = new[] { "preference" }
        });

        var result = await tool.ExecuteAsync(args, new ToolContext { SessionId = "s1" });

        result.Success.Should().BeTrue();
        savedPath.Should().StartWith("daily/");
        savedPath.Should().EndWith(".md");
        savedContent.Should().Contain("importance: 1.0");
        savedContent.Should().Contain("kind: explicit");
        savedContent.Should().Contain("默认使用暗色主题");
        memory.Verify(m => m.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MissingTitle_ShouldFail()
    {
        var services = new ServiceCollection();
        services.AddScoped<IMemoryService>(_ => Mock.Of<IMemoryService>());
        await using var sp = services.BuildServiceProvider();

        var tool = new MemoryWriteTool(NullLogger<MemoryWriteTool>.Instance, sp.GetRequiredService<IServiceScopeFactory>());
        var args = JsonSerializer.SerializeToElement(new { content = "只有内容" });

        var result = await tool.ExecuteAsync(args, new ToolContext());

        result.Success.Should().BeFalse();
    }
}
