using FluentAssertions;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>附件体积上限：超过 <see cref="TuiAttachmentResolver.MaxAttachmentBytes"/> 的文件被拒绝且错误可辨识。</summary>
public sealed class TuiAttachmentResolverSizeTests : IDisposable
{
    private readonly TuiAttachmentResolver _resolver = new();
    private readonly List<string> _tempFiles = [];

    [Fact]
    public void MaxAttachmentBytes_ShouldBeTenMegabytes()
        => TuiAttachmentResolver.MaxAttachmentBytes.Should().Be(10L * 1024 * 1024);

    [Fact]
    public async Task LoadAsync_OversizedFile_ShouldRejectWithIdentifiableMessage()
    {
        var path = CreateSparseTempFile(TuiAttachmentResolver.MaxAttachmentBytes + 1);

        var act = async () => await _resolver.LoadAsync(path);
        var exception = await act.Should().ThrowAsync<InvalidOperationException>();

        exception.Which.Message.Should().Contain("超过上限");
        exception.Which.Message.Should().Contain(Path.GetFileName(path));
        exception.Which.Message.Should().Contain("10 MB");
    }

    [Fact]
    public async Task LoadAsync_AtLimitFile_ShouldBeAccepted()
    {
        var path = CreateSparseTempFile(TuiAttachmentResolver.MaxAttachmentBytes);

        var attachment = await _resolver.LoadAsync(path);

        attachment.Size.Should().Be(TuiAttachmentResolver.MaxAttachmentBytes);
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (IOException)
            {
                // 临时文件清理失败不阻断测试
            }
        }
    }

    private string CreateSparseTempFile(long length)
    {
        var path = Path.Combine(Path.GetTempPath(), $"seeing-tui-size-{Guid.NewGuid():N}.bin");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(length);

        _tempFiles.Add(path);
        return path;
    }
}
