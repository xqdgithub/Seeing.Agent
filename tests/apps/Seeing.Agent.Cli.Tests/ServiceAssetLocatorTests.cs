using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class ServiceAssetLocatorTests
{
    [Theory]
    [InlineData("seeing-tui.dll", "Seeing.Agent.Tui")]
    [InlineData("Seeing.Agent.WebUI.dll", "Seeing.Agent.WebUI")]
    [InlineData("Seeing.Gateway.Server.dll", "Seeing.Gateway.Server")]
    public void ResolveProjectDirectoryName_ShouldMapAssemblyToProjectFolder(string dllName, string expected)
    {
        ServiceAssetLocator.ResolveProjectDirectoryName(dllName).Should().Be(expected);
    }

    [Fact]
    public void Find_WhenDllInCliDirectory_ShouldReturnIt()
    {
        using var temp = new TempDirectory();
        var dll = temp.CreateFile(ServiceAssetLocator.TuiDll);

        ServiceAssetLocator.Find(temp.Path, ServiceAssetLocator.TuiDll).Should().Be(dll);
    }

    [Fact]
    public void Find_WhenDllMissing_ShouldThrowWithBuildHint()
    {
        using var temp = new TempDirectory();

        var act = () => ServiceAssetLocator.Find(temp.Path, ServiceAssetLocator.TuiDll);

        act.Should().Throw<FileNotFoundException>().WithMessage("*dotnet build Seeing.Agent.slnx*");
    }

    [Fact]
    public void TryFindRepoRoot_ShouldWalkUpToSolutionDirectory()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(ServiceAssetLocator.SolutionFileName);
        var nested = temp.Combine("samples", "Seeing.Agent.Cli", "bin", "Debug", "net10.0");

        ServiceAssetLocator.TryFindRepoRoot(nested).Should().Be(temp.Path);
    }

    [Fact]
    public void TryFindRepoRoot_WithoutSolution_ShouldReturnNull()
    {
        using var temp = new TempDirectory();
        var nested = temp.Combine("a", "b", "c");

        ServiceAssetLocator.TryFindRepoRoot(nested).Should().BeNull();
    }

    /// <summary>
    /// 真实仓库布局下必须能定位 seeing-tui.dll。
    /// 依赖 CLI→TUI 的构建期引用（csproj ReferenceOutputAssembly=false），
    /// 因此构建本项目时 TUI 产物一定已存在。
    /// 仅投放测试二进制的场景（无源码树）不做断言。
    /// </summary>
    [Fact]
    public void Find_InRepositoryLayout_ShouldLocateBuiltTuiAssembly()
    {
        var repoRoot = ServiceAssetLocator.TryFindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
            return;

        var found = ServiceAssetLocator.Find(AppContext.BaseDirectory, ServiceAssetLocator.TuiDll);

        File.Exists(found).Should().BeTrue();
        Path.GetFileName(found).Should().Be(ServiceAssetLocator.TuiDll);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "seeing-cli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string fileName)
        {
            var full = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(full, string.Empty);
            return full;
        }

        public string Combine(params string[] parts)
        {
            var full = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
            Directory.CreateDirectory(full);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论。
            }
        }
    }
}
