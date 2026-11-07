using FluentAssertions;
using Seeing.Agent.Tools.Support.Speech;
using Xunit;

namespace Seeing.Agent.Tools.Speech.Tests;

public class AudioFormatSupportTests
{
    [Theory]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.wav", "audio/wav")]
    [InlineData("a.m4a", "audio/mp4")]
    [InlineData("a.mp4", "audio/mp4")]
    [InlineData("a.ogg", "audio/ogg")]
    [InlineData("a.opus", "audio/opus")]
    [InlineData("a.webm", "audio/webm")]
    [InlineData("a.flac", "audio/flac")]
    [InlineData("a.aac", "audio/aac")]
    [InlineData("a.amr", "audio/amr")]
    [InlineData("a.wma", "audio/x-ms-wma")]
    [InlineData("a.silk", "audio/silk")]
    public void TryGetMimeType_ShouldMap(string name, string expected)
    {
        AudioFormatSupport.TryGetMimeType(name, out var mime).Should().BeTrue();
        mime.Should().Be(expected);
    }

    [Theory]
    [InlineData("A.MP3")]
    [InlineData("C:\\dir\\Voice.WAV")]
    public void TryGetMimeType_ShouldIgnoreCaseAndDirectories(string name)
    {
        AudioFormatSupport.TryGetMimeType(name, out var mime).Should().BeTrue();
        mime.Should().StartWith("audio/");
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.png")]
    [InlineData("a")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetMimeType_ShouldRejectNonAudio(string? name)
    {
        AudioFormatSupport.TryGetMimeType(name, out var mime).Should().BeFalse();
        mime.Should().BeEmpty();
    }

    [Fact]
    public void MaxAudioBytes_ShouldBe25MB()
        => AudioFormatSupport.MaxAudioBytes.Should().Be(25L * 1024 * 1024);

    [Fact]
    public void SupportedExtensions_ShouldBeSortedAndNonEmpty()
    {
        AudioFormatSupport.SupportedExtensions.Should().NotBeEmpty();
        AudioFormatSupport.SupportedExtensions.Should()
            .BeInAscendingOrder(StringComparer.Ordinal);
    }
}
