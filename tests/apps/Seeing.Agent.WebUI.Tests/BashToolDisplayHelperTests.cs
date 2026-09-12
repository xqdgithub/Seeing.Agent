using FluentAssertions;
using Seeing.Agent.WebUI.Helpers;
using Seeing.Agent.WebUI.Models;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.WebUI.Tests;

public class BashToolDisplayHelperTests
{
    [Fact]
    public void StripBashMetadataBlock_ShouldRemoveTrailingBlock()
    {
        var input = "hello\n\n<bash_metadata>\ntimeout\n</bash_metadata>";
        BashToolDisplayHelper.StripBashMetadataBlock(input).Should().Be("hello");
    }

    [Fact]
    public void BashCardModel_From_ShouldMapArgsAndMetadata()
    {
        var vm = ToolCallViewModel.FromSessionToolCall(new SessionToolCall
        {
            Id = "1",
            Name = "bash",
            Title = "列目录",
            Arguments = """{"command":"ls","description":"列目录","workdir":"C:\\tmp"}""",
            Status = "success",
            Result = "a.txt",
            DurationMs = 1234.0,
            Metadata = new Dictionary<string, object>
            {
                ["exit"] = 0,
                ["timedOut"] = false,
                ["aborted"] = false
            }
        }, "s");

        var card = BashCardModel.From(vm);
        vm.IsBashTool.Should().BeTrue();
        card.Command.Should().Be("ls");
        card.Workdir.Should().Be(@"C:\tmp");
        card.DisplayTitle.Should().Be("列目录");
        card.ExitCode.Should().Be(0);
        vm.DurationMs.Should().Be(1234.0);
    }

    [Fact]
    public void FromSessionToolCall_ShouldReadLegacyDurationMsFromMetadata()
    {
        var vm = ToolCallViewModel.FromSessionToolCall(new SessionToolCall
        {
            Id = "1",
            Name = "bash",
            Status = "success",
            Metadata = new Dictionary<string, object> { ["durationMs"] = 99.0 }
        }, "s");
        vm.DurationMs.Should().Be(99.0);
    }

    [Fact]
    public void ShouldCollapseByDefault_LongOutputWhenFinished()
    {
        var longOut = string.Join('\n', Enumerable.Range(0, 50).Select(i => $"line{i}"));
        BashToolDisplayHelper.ShouldCollapseByDefault(longOut, isRunning: false).Should().BeTrue();
        BashToolDisplayHelper.ShouldCollapseByDefault(longOut, isRunning: true).Should().BeFalse();
    }

    [Fact]
    public void IsTaskTool_ShouldIgnoreBashEvenIfResultContainsTaskIdLiteral()
    {
        var vm = new ToolCallViewModel
        {
            Name = "bash",
            Result = "echo task_id:abc\ntask_id:abc"
        };
        vm.IsBashTool.Should().BeTrue();
        vm.IsTaskTool.Should().BeFalse();
    }

    [Fact]
    public void IsTaskTool_ShouldNotMatchBySubagentTypeInParametersAlone()
    {
        var vm = new ToolCallViewModel
        {
            Name = "read",
            Parameters = """{"subagent_type":"explore"}"""
        };
        vm.IsTaskTool.Should().BeFalse();
    }

    [Fact]
    public void IsTaskTool_ShouldMatchNameOrExplicitTaskId()
    {
        new ToolCallViewModel { Name = "task" }.IsTaskTool.Should().BeTrue();
        new ToolCallViewModel { Name = "other", TaskId = "sid" }.IsTaskTool.Should().BeTrue();
    }
}
