using FluentAssertions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class PromptAttachmentRouterTests
{
    [Fact]
    public void Route_Image_ShouldCreateChatAttachmentAndStripDataPrefix()
    {
        var route = PromptAttachmentRouter.Route("hi", [
            new AttachmentViewModel
            {
                Kind = AttachmentDeliveryKind.BinaryAttachment,
                Status = AttachmentUploadStatus.Completed,
                FileName = "a.png",
                FileType = "image/png",
                Base64Content = "data:image/png;base64,AQID"
            }
        ]);

        route.Attachments.Should().ContainSingle();
        route.Attachments[0].Base64Data.Should().Be("AQID");
        route.Text.Should().Be("hi");
    }

    [Fact]
    public void Route_PathReference_ShouldAppendBacktickedPathToText()
    {
        var route = PromptAttachmentRouter.Route("hi", [
            new AttachmentViewModel
            {
                Kind = AttachmentDeliveryKind.PathReference,
                Status = AttachmentUploadStatus.Completed,
                PromptPath = ".seeing/uploads/s1/a.pdf"
            }
        ]);

        route.Attachments.Should().BeEmpty();
        route.Text.Should().Be("hi\n`.seeing/uploads/s1/a.pdf`");
    }

    [Fact]
    public void Route_Mixed_ShouldSplitCorrectly()
    {
        var route = PromptAttachmentRouter.Route("", [
            new AttachmentViewModel
            {
                Kind = AttachmentDeliveryKind.BinaryAttachment,
                Status = AttachmentUploadStatus.Completed,
                FileType = "image/png",
                Base64Content = "AQID"
            },
            new AttachmentViewModel
            {
                Kind = AttachmentDeliveryKind.PathReference,
                Status = AttachmentUploadStatus.Completed,
                PromptPath = "a.txt"
            }
        ]);

        route.Attachments.Should().ContainSingle();
        route.Text.Should().Be("`a.txt`");
    }
}
