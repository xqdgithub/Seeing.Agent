using FluentAssertions;
using Seeing.Gateway.QQ;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQOutboundComposerTests
{
    [Fact]
    public void Compose_ShouldExtractImageVideoFileMarkers()
    {
        var raw = "说明 [Image: https://a/x.png] [Video: https://a/v.mp4] [File: https://a/d.pdf]";
        var payload = QQOutboundComposer.Compose(raw);
        payload.Media.Should().HaveCount(3);
        payload.Media[0].Kind.Should().Be(QQOutboundMediaKind.Image);
        payload.Media[1].Kind.Should().Be(QQOutboundMediaKind.Video);
        payload.Media[2].Kind.Should().Be(QQOutboundMediaKind.File);
        payload.CleanText.Should().Contain("说明");
        payload.CleanText.Should().NotContain("https://");
    }

    [Fact]
    public void Compose_ShouldExtractMarkdownImage()
    {
        var payload = QQOutboundComposer.Compose("见 ![x](https://cdn.example.com/b.jpg)");
        payload.Media.Should().ContainSingle(m => m.Kind == QQOutboundMediaKind.Image && m.Source.Contains("b.jpg"));
        payload.CleanText.Should().Be("见");
    }

    [Fact]
    public void ResolveMediaType_VoiceExt_ShouldBeAudio()
    {
        QQOutboundComposer.ResolveMediaType(QQOutboundMediaKind.File, "a.silk").Should().Be(3);
        QQOutboundComposer.ResolveMediaType(QQOutboundMediaKind.Image, "x.png").Should().Be(1);
        QQOutboundComposer.ResolveMediaType(QQOutboundMediaKind.Video, "x.mp4").Should().Be(2);
    }
}
