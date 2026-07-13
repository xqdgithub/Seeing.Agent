using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Models;
using Seeing.Gateway.WeCom;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComQuoteParserTests
{
    [Fact]
    public async Task ParseAsync_NullQuote_ShouldReturnNull()
    {
        var fetcher = CreateMediaFetcher();
        var result = await WeComQuoteParser.ParseAsync(null, fetcher, NullLogger.Instance);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_TextQuote_ShouldReturnGatewayQuoteContext()
    {
        var fetcher = CreateMediaFetcher();
        var quote = new WeComQuotePayload
        {
            MsgType = "text",
            Text = new WeComTextPayload { Content = "  被引用内容  " }
        };

        var result = await WeComQuoteParser.ParseAsync(quote, fetcher, NullLogger.Instance);

        result.Should().NotBeNull();
        result!.MsgType.Should().Be("text");
        result.SourceChannel.Should().Be("wecom");
        result.Content.Should().ContainSingle()
            .Which.Should().BeOfType<GatewayTextContentPart>()
            .Which.Text.Should().Be("被引用内容");
    }

    [Fact]
    public async Task ParseAsync_VoiceQuoteWithTranscription_ShouldReturnTextPart()
    {
        var fetcher = CreateMediaFetcher();
        var quote = new WeComQuotePayload
        {
            MsgType = "voice",
            Voice = new WeComVoicePayload { Content = "语音转写内容" }
        };

        var result = await WeComQuoteParser.ParseAsync(quote, fetcher, NullLogger.Instance);

        result!.Content.Should().ContainSingle()
            .Which.Should().BeOfType<GatewayTextContentPart>()
            .Which.Text.Should().Be("语音转写内容");
    }

    [Fact]
    public async Task ParseAsync_MixedQuote_ShouldReturnMultipleParts()
    {
        var fetcher = CreateMediaFetcher();
        var quote = new WeComQuotePayload
        {
            MsgType = "mixed",
            Mixed = new WeComMixedPayload
            {
                MsgItem =
                [
                    new WeComMixedMessageItem
                    {
                        MsgType = "text",
                        Text = new WeComTextPayload { Content = "第一段" }
                    },
                    new WeComMixedMessageItem
                    {
                        MsgType = "text",
                        Text = new WeComTextPayload { Content = "第二段" }
                    }
                ]
            }
        };

        var result = await WeComQuoteParser.ParseAsync(quote, fetcher, NullLogger.Instance);

        result!.MsgType.Should().Be("mixed");
        result.Content.Should().HaveCount(2);
        result.Content!.OfType<GatewayTextContentPart>().Select(p => p.Text)
            .Should().Equal("第一段", "第二段");
    }

    private static WeComMediaFetcher CreateMediaFetcher()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(WeComMediaFetcher.HttpClientName);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new WeComMediaFetcher(
            factory,
            Options.Create(new WeComOptions()),
            NullLogger<WeComMediaFetcher>.Instance);
    }
}

public class WeComGroupMentionNormalizerTests
{
    [Fact]
    public void NormalizeUserText_GroupChat_ShouldStripLeadingMention()
    {
        WeComGroupMentionNormalizer.NormalizeUserText("@Bot 数据来源是什么", "group")
            .Should().Be("数据来源是什么");
    }

    [Fact]
    public void NormalizeUserText_GroupChat_ShouldStripMentionBeforeSlashCommand()
    {
        WeComGroupMentionNormalizer.NormalizeUserText("@Bot /clear", "group")
            .Should().Be("/clear");
    }

    [Fact]
    public void NormalizeUserText_SingleChat_ShouldPreserveMention()
    {
        WeComGroupMentionNormalizer.NormalizeUserText("@Bot hello", "single")
            .Should().Be("@Bot hello");
    }
}
