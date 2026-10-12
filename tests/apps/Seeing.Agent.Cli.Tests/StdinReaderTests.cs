using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class StdinReaderTests
{
    [Fact]
    public async Task ReadAllAsync_WithContent_ShouldReturnTrimmedPresence()
    {
        var reader = new StringReader("  hello world  ");

        var result = await StdinReader.ReadAllAsync(reader, TestContext.Current.CancellationToken);

        result.Should().Be("  hello world  ");
    }

    [Fact]
    public async Task ReadAllAsync_WithBlankContent_ShouldReturnNull()
    {
        var reader = new StringReader("   \n  ");

        var result = await StdinReader.ReadAllAsync(reader, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadAllAsync_WithEmpty_ShouldReturnNull()
    {
        var result = await StdinReader.ReadAllAsync(new StringReader(string.Empty), TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }
}
