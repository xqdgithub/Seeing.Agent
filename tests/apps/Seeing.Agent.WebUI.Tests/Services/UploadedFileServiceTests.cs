using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.Configuration;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Services;

public class UploadedFileServiceTests
{
    private static (UploadedFileService Service, Mock<IFileWorkspaceFactory> Factory, Mock<IFileWorkspace> Workspace) Create(string root)
    {
        var workspace = new Mock<IFileWorkspace>();
        workspace.SetupGet(w => w.RootPath).Returns(root);
        workspace.Setup(w => w.WriteBytesAsync(
                It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        workspace.Setup(w => w.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var factory = new Mock<IFileWorkspaceFactory>();
        factory.Setup(f => f.ResolveAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspace.Object);

        var project = new Mock<IWorkspaceProvider>();
        project.SetupGet(p => p.ProjectSeeingDirectory).Returns(Path.Combine(root, ".seeing"));

        return (new UploadedFileService(factory.Object, project.Object), factory, workspace);
    }

    [Fact]
    public async Task StoreAsync_ShouldWriteUnderSessionUploadsAndComputeRelativePromptPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "upload-test-" + Guid.NewGuid().ToString("N"));
        var (service, factory, workspace) = Create(root);
        string? written = null;
        workspace.Setup(w => w.WriteBytesAsync(
                It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, ReadOnlyMemory<byte>, CancellationToken>((path, _, _) => written = path)
            .Returns(Task.CompletedTask);

        var stored = await service.StoreAsync("s1", "a.txt", "text/plain", new byte[] { 1, 2, 3 });

        stored.Kind.Should().Be(AttachmentDeliveryKind.PathReference);
        stored.SessionRelativePath.Should().StartWith(".seeing/uploads/s1/");
        stored.PromptPath.Should().Be(stored.SessionRelativePath);
        Path.GetFullPath(Path.Combine(root, stored.SessionRelativePath)).Should().Be(written);
        factory.Verify(f => f.Release("s1"), Times.Once);
    }

    [Fact]
    public async Task CreatePathReferenceAsync_TraversalPath_ShouldThrowWithoutReadOrDelete()
    {
        var root = Path.Combine(Path.GetTempPath(), "upload-test-" + Guid.NewGuid().ToString("N"));
        var (service, _, workspace) = Create(root);
        var item = new UploadedFileItem("file", "a.txt", "../outside.txt", 1, "text/plain");

        var act = () => service.CreatePathReferenceAsync("s1", item);

        await act.Should().ThrowAsync<InvalidOperationException>();
        workspace.Verify(w => w.ReadBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        workspace.Verify(w => w.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateImageAttachmentAsync_ShouldReadDeleteAndBuildDataUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "upload-test-" + Guid.NewGuid().ToString("N"));
        var (service, _, workspace) = Create(root);
        workspace.Setup(w => w.ReadBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });
        var item = new UploadedFileItem(
            "image",
            "a.png",
            ".seeing/uploads/s1/a.png",
            3,
            "image/png");

        var attachment = await service.CreateImageAttachmentAsync("s1", item);

        attachment.Kind.Should().Be(AttachmentDeliveryKind.BinaryAttachment);
        attachment.Base64Content.Should().Be("data:image/png;base64,AQID");
        workspace.Verify(w => w.DeleteAsync(
            It.Is<string>(p => p.EndsWith("a.png", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
