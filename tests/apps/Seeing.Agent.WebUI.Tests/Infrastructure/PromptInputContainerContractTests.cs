using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

public class PromptInputContainerContractTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("无法定位仓库根目录");
    }

    [Fact]
    public void PromptInput_JSHandlers_ShouldUseRealDomId()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot, "samples", "Seeing.Agent.WebUI", "Components", "PromptInput.razor"));

        // ElementReference.Id 是 Blazor 内部的 _bl_* 标记，不是 DOM id；
        // document.getElementById 必须拿到真实 id，否则粘贴/拖拽监听器根本不会绑定。
        source.Should().Contain("id=\"@ContainerId\"");
        source.Should().Contain("\"setupPasteHandler\", ContainerId");
        source.Should().Contain("\"setupDropZone\", ContainerId");
        source.Should().NotContain("ContainerRef.Id");
    }
}
