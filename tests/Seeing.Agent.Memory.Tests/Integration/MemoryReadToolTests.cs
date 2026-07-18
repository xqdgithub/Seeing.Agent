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

public class MemoryReadToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithRelativePath_ShouldReturnBody()
    {
        var memory = new Mock<IMemoryService>();
        memory
            .Setup(m => m.GetAsync("daily/2026-07-18/explicit-abc.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileNode.Create(
                "daily/2026-07-18/explicit-abc.md",
                """
                ---
                id: abc
                title: "回答偏好"
                importance: 1.0
                ---

                请用简体中文回答
                """,
                FileMetadata.Create("abc", MemoryType.Daily, "回答偏好") with { Importance = 1.0 }));

        var services = new ServiceCollection();
        services.AddScoped(_ => memory.Object);
        await using var sp = services.BuildServiceProvider();

        var tool = new MemoryReadTool(NullLogger<MemoryReadTool>.Instance, sp.GetRequiredService<IServiceScopeFactory>());
        var args = JsonSerializer.SerializeToElement(new { path = "daily/2026-07-18/explicit-abc.md" });

        var result = await tool.ExecuteAsync(args, new ToolContext());

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("回答偏好");
        result.Output.Should().Contain("请用简体中文回答");
    }

    [Fact]
    public async Task ExecuteAsync_WithWrongAbsolutePath_ShouldStripToRelative()
    {
        var memory = new Mock<IMemoryService>();
        memory
            .Setup(m => m.GetAsync("daily/2026-07-18/explicit-abc.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(FileNode.Create(
                "daily/2026-07-18/explicit-abc.md",
                "body",
                FileMetadata.Create("abc", MemoryType.Daily, "t")));

        var services = new ServiceCollection();
        services.AddScoped(_ => memory.Object);
        await using var sp = services.BuildServiceProvider();

        var tool = new MemoryReadTool(NullLogger<MemoryReadTool>.Instance, sp.GetRequiredService<IServiceScopeFactory>());
        var args = JsonSerializer.SerializeToElement(new
        {
            path = @"C:\Users\quand\.agents\memory\daily\2026-07-18\explicit-abc.md"
        });

        var result = await tool.ExecuteAsync(args, new ToolContext());

        result.Success.Should().BeTrue();
        memory.Verify(m => m.GetAsync("daily/2026-07-18/explicit-abc.md", It.IsAny<CancellationToken>()), Times.Once);
    }
}
