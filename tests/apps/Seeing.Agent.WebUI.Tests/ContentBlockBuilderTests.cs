using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Models.Messaging;

namespace Seeing.Agent.WebUI.Tests;

/// <summary>
/// 内容块构建器测试：多模态消息的文本段不得被误当作附件渲染。
/// </summary>
public class ContentBlockBuilderTests
{
    private static ContentPartViewModel TextPart(string text) => new()
    {
        Type = "text",
        Text = text
    };

    private static ContentPartViewModel ImagePart(string base64, string mime = "image/png") => new()
    {
        Type = "image",
        DataBase64 = base64,
        MimeType = mime
    };

    [Fact]
    public void BuildFromMessage_UserMessageWithTextAndImage_ShouldKeepTextAsTextBlock()
    {
        // 带附件的用户消息：文本存于 Parts 的 text 段，Content 为空
        var message = new MessageViewModel
        {
            Role = "user",
            Content = "",
            IsComplete = true,
            Parts = new List<ContentPartViewModel>
            {
                TextPart("这是什么"),
                ImagePart("AAAA")
            }
        };

        var blocks = ContentBlockBuilder.BuildFromMessage(message);

        blocks.Should().HaveCount(2);
        blocks[0].Type.Should().Be(ContentBlockType.Text);
        blocks[0].Content.Should().Be("这是什么");
        blocks[1].Type.Should().Be(ContentBlockType.Image);
        blocks.Should().NotContain(b => b.Type == ContentBlockType.Attachment);
    }

    [Fact]
    public void BuildFromMessage_OldPlainTextMessage_ShouldRenderSingleTextBlock()
    {
        var message = new MessageViewModel
        {
            Role = "user",
            Content = "纯文本",
            IsComplete = true
        };

        var blocks = ContentBlockBuilder.BuildFromMessage(message);

        blocks.Should().ContainSingle().Which.Type.Should().Be(ContentBlockType.Text);
        blocks[0].Content.Should().Be("纯文本");
    }

    [Fact]
    public void BuildFromMessage_AttachmentOnlyMessage_ShouldNotAddEmptyTextBlock()
    {
        var message = new MessageViewModel
        {
            Role = "user",
            Content = "",
            IsComplete = true,
            Parts = new List<ContentPartViewModel> { ImagePart("AAAA") }
        };

        var blocks = ContentBlockBuilder.BuildFromMessage(message);

        blocks.Should().ContainSingle().Which.Type.Should().Be(ContentBlockType.Image);
    }
}
