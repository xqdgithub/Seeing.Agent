using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Files.Tests;

/// <summary>
/// 错误契约映射测试（设计规格 §错误契约 UI 映射）。
/// 直接使用契约层异常类型（<c>Abstractions.Files</c>），锁定强类型映射契约。
/// </summary>
public class FileErrorPresenterTests
{
    [Fact]
    public void UnauthorizedAccess_ShouldMapToWorkspaceMessage()
    {
        FileErrorPresenter.Describe(new UnauthorizedAccessException("越界"))
            .Should().Be("路径不在工作区");
    }

    [Fact]
    public void FileNotFound_ShouldMapToMissingMessage()
    {
        FileErrorPresenter.Describe(new FileNotFoundException("没了"))
            .Should().Be("文件不存在");
    }

    [Fact]
    public void DirectoryNotFound_ShouldMapToMissingMessage()
    {
        FileErrorPresenter.Describe(new DirectoryNotFoundException("没了"))
            .Should().Be("文件不存在");
    }

    [Fact]
    public void FileTooLarge_ShouldMapToPreviewOnlyMessage()
    {
        FileErrorPresenter.Describe(new FileTooLargeException("a.bin", 999, 5))
            .Should().Be("文件过大，仅预览");
    }

    [Fact]
    public void FileNotEditable_ShouldMapToEncodingMessage()
    {
        FileErrorPresenter.Describe(new FileNotEditableException("a.txt", "非 UTF-8"))
            .Should().Be("该文件编码不支持编辑");
    }

    [Fact]
    public void IOException_ShouldMapToRetryMessage()
    {
        FileErrorPresenter.Describe(new IOException("占用中"))
            .Should().Be("读取失败，请重试");
    }

    [Fact]
    public void OperationCanceled_ShouldMapToCanceledMessage()
    {
        FileErrorPresenter.Describe(new OperationCanceledException())
            .Should().Be("操作已取消");
    }

    [Fact]
    public void UnknownException_ShouldMapToGenericMessage()
    {
        FileErrorPresenter.Describe(new InvalidOperationException("???"))
            .Should().Be("无法打开该文件");
    }
}
