using FluentAssertions;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 引擎附件映射接线测试：<see cref="TuiAttachment"/> → <c>ChatAttachment</c> 的纯映射契约。
/// </summary>
public sealed class TuiChatEngineAttachmentTests
{
    [Fact]
    public void ToChatAttachments_ShouldMapFileNameMimeAndBase64()
    {
        var attachments = new[]
        {
            new TuiAttachment("a.png", "image/png", "AQID", 3),
            new TuiAttachment("b.txt", "text/plain", "eA==", 1),
        };

        var mapped = TuiChatEngine.ToChatAttachments(attachments);

        mapped.Should().HaveCount(2);
        mapped[0].FileName.Should().Be("a.png");
        mapped[0].MimeType.Should().Be("image/png");
        mapped[0].Base64Data.Should().Be("AQID");
        mapped[1].FileName.Should().Be("b.txt");
        mapped[1].Base64Data.Should().Be("eA==");
    }

    [Fact]
    public void ToChatAttachments_Empty_ShouldReturnEmptyList()
        => TuiChatEngine.ToChatAttachments([]).Should().BeEmpty();
}
