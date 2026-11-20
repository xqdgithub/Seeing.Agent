using FluentAssertions;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.WebUI.Models;

namespace Seeing.Agent.WebUI.Tests.Models;

/// <summary>
/// 后台活动卡片的输出统计投影：大小与截断只读快照强类型字段，
/// 不得再从 <see cref="BackgroundActivitySnapshot.Attributes"/> 的约定键猜测。
/// </summary>
public class BackgroundActivityViewModelOutputTests
{
    [Fact]
    public void WebUi_ShouldReadOutputBytesFromSnapshot()
    {
        var snapshot = Snapshot() with
        {
            OutputBytes = 2048,
            OutputTruncated = true,
            OutputLimitBytes = 5 * 1024 * 1024,

            // 旧约定的兜底键故意写成不一致的值：UI 必须完全忽略它们。
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["output_bytes"] = "999999",
                ["bytes_written"] = "888888",
                ["output.size"] = "777777"
            }
        };

        var viewModel = BackgroundActivityViewModel.FromSnapshot(snapshot);

        viewModel.OutputBytes.Should().Be(2048);
        viewModel.OutputTruncated.Should().BeTrue();
        viewModel.OutputSizeText.Should().Be("2.0 KB");
        viewModel.OutputTruncatedText.Should().Be("已截断");
    }

    [Fact]
    public void WebUi_WithoutOutputBytes_ShouldNotGuessFromAttributes()
    {
        var snapshot = Snapshot() with
        {
            OutputLimitBytes = 1024,
            Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["output_bytes"] = "4096"
            }
        };

        var viewModel = BackgroundActivityViewModel.FromSnapshot(snapshot);

        viewModel.OutputBytes.Should().BeNull();
        viewModel.OutputSizeText.Should().BeNull("快照没有实际字节数时不显示猜测值");
        viewModel.OutputLimitText.Should().Be("上限 1.0 KB");
    }

    private static BackgroundActivitySnapshot Snapshot() => new()
    {
        Id = "act_0123456789abcdef0123456789abcdef",
        Kind = "process",
        Title = "后台命令",
        WorkspaceRoot = ".",
        HostInstanceId = "host",
        OwnerSessionId = "s1",
        OwnerExecutionId = "e1",
        Status = BackgroundActivityStatus.Succeeded,
        Capabilities = BackgroundActivityCapabilities.CanStreamOutput
    };
}
