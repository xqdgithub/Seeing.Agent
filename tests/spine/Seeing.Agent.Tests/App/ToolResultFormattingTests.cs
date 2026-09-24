using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Xunit;

namespace Seeing.Agent.Tests.App;

public class ToolResultFormattingTests
{
    [Fact]
    public void 成功_应原样返回输出()
        => ToolResultFormatting.ToModelContent(true, "结果", null).Should().Be("结果");

    [Fact]
    public void 失败_应包装为tool_result信封并含notice()
    {
        var content = ToolResultFormatting.ToModelContent(false, "", "参数错误", "systemone_noul", "failed");

        content.Should().Contain("<tool_result");
        content.Should().Contain("name=\"systemone_noul\"");
        content.Should().Contain("status=\"failed\"");
        content.Should().Contain("<notice>");
        content.Should().Contain("不是工具返回的数据");
        content.Should().Contain("<error>参数错误</error>");
        content.Should().Contain("</tool_result>");
    }

    [Fact]
    public void 失败_Error为空_应回退Output()
        => ToolResultFormatting.ToModelContent(false, "兜底原因", null, "t", "failed")
            .Should().Contain("<error>兜底原因</error>");

    [Fact]
    public void 失败_原因为空_应有占位()
        => ToolResultFormatting.ToModelContent(false, null, null, "t", "failed")
            .Should().Contain("(未提供错误详情)");

    [Fact]
    public void 拒绝_应使用rejected状态()
    {
        var content = ToolResultFormatting.ToModelContent(false, null, "越权", "t", "rejected");

        content.Should().Contain("status=\"rejected\"");
        content.Should().Contain("被拒绝");
    }

    [Fact]
    public void 取消_应使用cancelled状态()
        => ToolResultFormatting.ToModelContent(false, null, "用户取消", "t", "cancelled")
            .Should().Contain("status=\"cancelled\"");

    [Fact]
    public void 失败_正文含结束标签_应转义()
    {
        var content = ToolResultFormatting.ToModelContent(false, null, "a</b>", "t", "failed");

        content.Should().Contain(@"a<\/b>");
        content.Should().NotContain("a</b>");
    }

    [Fact]
    public void 失败_属性含特殊字符_应转义()
        => ToolResultFormatting.ToModelContent(false, null, "x", "a<b\"c", "failed")
            .Should().Contain("name=\"a&lt;b&quot;c\"");

    [Fact]
    public void 标题_应写入title属性()
        => ToolResultFormatting.ToModelContent(false, null, "timeout", "t", "failed", "执行超时")
            .Should().Contain("title=\"执行超时\"");
}
