using System.Text.Json;
using FluentAssertions;
using Seeing.Gateway.QQ;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQMessageParserQuoteAndVoiceTests
{
    [Fact]
    public void TryParse_QuotedMessage_ShouldPrependQuotedText()
    {
        var json = """
        {
          "id": "msg3",
          "content": "reply here",
          "author": { "user_openid": "u1" },
          "message_scene": { "ext": ["msg_idx=2", "ref_msg_idx=1"] },
          "msg_elements": [
            { "msg_idx": "1", "content": "original quote" },
            { "msg_idx": "2", "content": "reply here" }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("C2C_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.Text.Should().StartWith("[quoted message: original quote]");
        msg.Text.Should().Contain("reply here");
    }

    [Fact]
    public void TryParse_VoiceAttachment_ShouldCaptureAsrAndWavUrl()
    {
        var json = """
        {
          "id": "msg4",
          "content": "",
          "author": { "user_openid": "u1" },
          "attachments": [
            {
              "url": "https://example.com/v.amr",
              "filename": "voice.amr",
              "content_type": "voice",
              "asr_refer_text": "你好世界",
              "voice_wav_url": "https://example.com/v.wav"
            }
          ]
        }
        """;
        using var doc = JsonDocument.Parse(json);

        QQMessageParser.TryParse("C2C_MESSAGE_CREATE", doc.RootElement, out var msg).Should().BeTrue();
        msg!.Attachments.Should().HaveCount(1);
        var att = msg.Attachments[0];
        att.IsVoice.Should().BeTrue();
        att.AsrText.Should().Be("你好世界");
        att.Url.Should().Be("https://example.com/v.wav");
        att.FileName.Should().EndWith(".wav");
    }
}
