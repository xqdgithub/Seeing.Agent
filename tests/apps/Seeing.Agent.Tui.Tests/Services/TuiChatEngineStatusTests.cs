using FluentAssertions;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.TokenBudget.Api.Responses;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.Tests.Services;

/// <summary>
/// 状态栏数据源测试：工作目录解析与上下文用量映射。
/// </summary>
public sealed class TuiChatEngineStatusTests
{
    [Fact]
    public void ResolveWorkspaceRoot_WithProvider_ShouldReturnProjectRoot()
    {
        var root = Path.Combine("D:\\", "projects", "Seeing.Agent");
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(Path.Combine(root, ".seeing"));

        TuiChatEngine.ResolveWorkspaceRoot(workspace.Object).Should().Be(root);
    }

    [Fact]
    public void ResolveWorkspaceRoot_WithNullProvider_ShouldFallbackToCurrentDirectory()
        => TuiChatEngine.ResolveWorkspaceRoot(null).Should().Be(Directory.GetCurrentDirectory());

    [Fact]
    public void ResolveWorkspaceRoot_WhenProviderThrows_ShouldFallbackToCurrentDirectory()
    {
        // 工作区未初始化时取值会抛：状态栏属装饰性信息，必须回落而非冒泡。
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.ProjectSeeingDirectory).Throws(new InvalidOperationException("未初始化"));

        TuiChatEngine.ResolveWorkspaceRoot(workspace.Object).Should().Be(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void ResolveWorkspaceRoot_WhenProviderReturnsBlank_ShouldFallbackToCurrentDirectory()
    {
        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns("   ");

        TuiChatEngine.ResolveWorkspaceRoot(workspace.Object).Should().Be(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void ToBudget_WhenStatusMissingOrEmpty_ShouldReturnNull()
    {
        TuiChatEngine.ToBudget(null).Should().BeNull();
        TuiChatEngine.ToBudget(new BudgetStatusResponse { CurrentTokens = 0, MaxTokens = 0 }).Should().BeNull();
    }

    [Fact]
    public void ToBudget_WithStatus_ShouldMapCurrentAndMax()
    {
        var budget = TuiChatEngine.ToBudget(new BudgetStatusResponse { CurrentTokens = 120, MaxTokens = 400 });

        budget.Should().NotBeNull();
        budget!.CurrentTokens.Should().Be(120);
        budget.MaxTokens.Should().Be(400);
    }

    [Fact]
    public void ToBudget_WithoutMaxTokens_ShouldLeaveLimitUnknown()
    {
        var budget = TuiChatEngine.ToBudget(new BudgetStatusResponse { CurrentTokens = 120, MaxTokens = 0 });

        budget.Should().NotBeNull();
        budget!.CurrentTokens.Should().Be(120);
        budget.MaxTokens.Should().BeNull();
    }
}
