using FluentAssertions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Infrastructure;

public class UploadScriptContractTests
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

    private static string AppJs => File.ReadAllText(
        Path.Combine(RepoRoot, "samples", "Seeing.Agent.WebUI", "wwwroot", "js", "app.js"));

    [Fact]
    public void AppJs_ShouldDefineUploadFilesAndPasteDropHandlers()
    {
        AppJs.Should().Contain("async function uploadFiles(");
        AppJs.Should().Contain("function setupPasteHandler(");
        AppJs.Should().Contain("function setupDropZone(");
        AppJs.Should().Contain("disposePasteHandler");
        AppJs.Should().Contain("disposeDropZone");
    }

    [Fact]
    public void AppJs_ShouldNotBase64FilesBeforeInterop()
    {
        AppJs.Should().NotContain("fileToBase64(file)");
        AppJs.Should().Contain("/api/uploads");
        AppJs.Should().Contain("FormData");
    }
}
