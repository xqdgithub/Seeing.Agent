using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Llm;
using Seeing.Agent.Memory.Configuration;
using Seeing.Agent.Memory.Core.Evolution;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Evolution;

public class LlmMemoryExtractorTests
{
    [Fact]
    public async Task ExtractAsync_WhenImportanceBelowThreshold_ShouldReturnNull()
    {
        var completion = new Mock<ITextCompletion>();
        completion.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"title":"t","content":"c","importance":0.2,"tags":[],"kind":"fact"}""");

        var extractor = new LlmMemoryExtractor(
            completion.Object,
            Options.Create(new MemoryOptions
            {
                Extraction = new MemoryExtractionOptions { MinImportance = 0.5, Model = "m" }
            }),
            NullLogger<LlmMemoryExtractor>.Instance);

        var result = await extractor.ExtractAsync(
            new MemoryCandidate("1", "s", null, MemorySource.Chat, null, "用户喜欢 PostgreSQL 并且要分页", DateTimeOffset.UtcNow),
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_WhenLlmThrows_ShouldReturnNull()
    {
        var completion = new Mock<ITextCompletion>();
        completion.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var extractor = new LlmMemoryExtractor(
            completion.Object,
            Options.Create(new MemoryOptions
            {
                Extraction = new MemoryExtractionOptions { Model = "m" }
            }),
            NullLogger<LlmMemoryExtractor>.Instance);

        var result = await extractor.ExtractAsync(
            new MemoryCandidate("1", "s", null, MemorySource.Chat, null, "用户喜欢 PostgreSQL 并且要分页", DateTimeOffset.UtcNow),
            default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExtractAsync_WhenValid_ShouldReturnResult()
    {
        var completion = new Mock<ITextCompletion>();
        completion.Setup(x => x.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"title":"DB","content":"User prefers PostgreSQL","importance":0.9,"tags":["db"],"kind":"preference"}""");

        var extractor = new LlmMemoryExtractor(
            completion.Object,
            Options.Create(new MemoryOptions
            {
                Extraction = new MemoryExtractionOptions { Model = "m", MinImportance = 0.5 }
            }));

        var result = await extractor.ExtractAsync(
            new MemoryCandidate("1", "s", null, MemorySource.Chat, null, "用户喜欢 PostgreSQL 并且要分页", DateTimeOffset.UtcNow),
            default);

        result.Should().NotBeNull();
        result!.Importance.Should().Be(0.9);
        result.Kind.Should().Be("preference");
    }
}
