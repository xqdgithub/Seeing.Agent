using FluentAssertions;
using Seeing.Agent.Abstractions.Files;
using Seeing.Agent.WebUI.Files.Services;
using Seeing.Agent.WebUI.Files.Workbenches;
using Xunit;

namespace Seeing.Agent.WebUI.Files.Tests;

/// <summary>
/// 文件工作台的上下文上报内容。
/// <para>
/// 合并掉独立 MD 工作台后，「agent 知道用户正在看哪个文档」全靠这里。
/// 摘要进模型上下文，措辞与可恢复状态都必须稳定。
/// </para>
/// </summary>
public class FilesWorkbenchReportsTests
{
    private static OpenFile Open(string path, bool preview) => new()
    {
        Id = new FileResourceId("root", path),
        Content = "# hi",
        Kind = FileContentKind.Markdown,
        PreviewMode = preview
    };

    [Fact]
    public void For_PreviewMode_ShouldSummarizeAsPreviewing()
    {
        var report = FilesWorkbenchReports.For(Open("docs/a.md", preview: true));

        report.ContextSummary.Should().Be("正在预览 docs/a.md");
    }

    [Fact]
    public void For_EditMode_ShouldSummarizeAsEditing()
    {
        var report = FilesWorkbenchReports.For(Open("docs/a.md", preview: false));

        report.ContextSummary.Should().Be("正在编辑 docs/a.md");
    }

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public void For_ShouldEncodePreviewFlagForRestore(bool preview, string expected)
    {
        var report = FilesWorkbenchReports.For(Open("a.md", preview));

        report.ViewState.Should().NotBeNull();
        report.ViewState!["preview"].Should().Be(expected);
    }

    [Fact]
    public void For_ShouldRecordPathForRestore()
    {
        var report = FilesWorkbenchReports.For(Open("src/deep/b.md", preview: true));

        report.ViewState!["path"].Should().Be("src/deep/b.md",
            "刷新后要能重开同一个文件");
    }
}
