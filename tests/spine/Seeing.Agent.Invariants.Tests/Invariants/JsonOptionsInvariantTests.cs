using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Invariants.Tests.Invariants;

/// <summary>
/// 可读 JSON 统一护栏：生产代码不得裸写 <c>new JsonSerializerOptions</c>，必须走
/// <c>Seeing.Serialization.SeeingJson</c>。否则中文会重新变成 <c>\uXXXX</c>，并把
/// 「签名/哈希字节稳定」与「可读输出」两条边界重新搅混。
/// <para>唯一豁免：<c>SeeingJson</c> 自身（它就是选项工厂）。</para>
/// </summary>
public class JsonOptionsInvariantTests
{
    private const string SeeingJsonFile = "SeeingJson.cs";

    [Fact]
    public void Production_Code_Should_Not_Construct_JsonSerializerOptions_Directly()
    {
        var root = FindRepoRoot();
        var scanRoots = new[]
        {
            Path.Combine(root, "src"),
            Path.Combine(root, "plugs"),
            Path.Combine(root, "samples"),
        };

        var hits = new List<string>();
        foreach (var scanRoot in scanRoots)
            CollectHits(scanRoot, hits);

        hits.Should().BeEmpty(
            "生产代码必须通过 SeeingJson 构造 JSON 选项（唯一豁免 {0}）；命中：{1}",
            SeeingJsonFile,
            string.Join("; ", hits));
    }

    [Fact]
    public void SeeingJson_Should_Live_In_Primitives_Layer()
    {
        // 落点约束：Seeing.Session 是依赖地板，Abstractions/Core 都依赖它。
        // 若把工具上移到 Abstractions，会话存储就用不上，统一就失效了。
        var root = FindRepoRoot();
        var expected = Path.Combine(
            root, "src", "primitives", "Seeing.Session", "Serialization", "SeeingJson.cs");

        File.Exists(expected).Should().BeTrue(
            "SeeingJson 必须位于依赖地板 Seeing.Session，否则会话落盘无法复用；期望路径 {0}", expected);
    }

    private static void CollectHits(string directory, List<string> hits)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            // 跳过构建产物
            var relative = Path.GetRelativePath(directory, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            if (string.Equals(Path.GetFileName(file), SeeingJsonFile, StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            if (text.Contains("new JsonSerializerOptions", StringComparison.Ordinal))
                hits.Add(relative);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
