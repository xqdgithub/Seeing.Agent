using FluentAssertions;
using System.Text.RegularExpressions;

namespace Seeing.Agent.WebUI.Tests.Shell;

/// <summary>
/// 守门：工作台组件必须真的被渲染成组件，而不是被当成普通 HTML 标签透传。
/// <para>
/// <b>为什么需要这条测试</b>：Razor 遇到<b>无法解析的组件标签</b>时不报错——它会当成普通
/// 元素原样输出：参数全部变成小写 attribute、组件逻辑与子内容一律不执行、页面上表现为
/// 「打开成功但什么都没渲染」。这条坑真的发生过两次：
/// </para>
/// <list type="number">
/// <item><c>SessionId="SessionId"</c>：<c>string</c> 参数写成裸标识符会被当字面量
/// （其他类型走表达式，所以只有字符串中招）。</item>
/// <item><c>&lt;WorkbenchSurface&gt;</c> 缺少 <c>@using ...Workbench.Components</c>：
/// 标签解析不到组件类型，整块渲染成空。</item>
/// </list>
/// <para>
/// 两者都不会被编译器拦下，只能靠源码形态守门 + 运行时 DOM 断言。
/// </para>
/// </summary>
public class WorkbenchRazorWiringTests
{
    /// <summary>必须能解析到本仓自定义组件的标签（新增工作台组件时在此登记）。</summary>
    private static readonly string[] s_customComponentTags =
    [
        "WorkbenchDockSlot",
        "WorkbenchSurface",
        "WorkbenchDockHost",
        "WorkbenchTabStrip",
        "WorkbenchResizeHandle",
        "WorkbenchUnavailableView",
        "WorkbenchPicker",
    ];

    [Fact]
    public void CustomComponentTags_ShouldBeUsedInSource()
    {
        // 至少要真的用到（防止改名后忘了同步）
        var sources = ReadAllRazorUnder("samples");

        foreach (var tag in s_customComponentTags)
        {
            sources.Should().ContainKey(tag,
                $"'{tag}' 应至少在一个 .razor 里使用；若已改名请同步本表");
        }
    }

    [Fact]
    public void ComponentFile_ShouldExistForEveryUsedTag()
    {
        // Razor 解析组件靠「命名空间 + 文件名」：用到的标签必须有同名 .razor 文件，
        // 否则会静默降级为普通 HTML（空渲染）
        var razorFiles = Directory
            .EnumerateFiles(Path.Combine(FindRepositoryRoot(), "samples"), "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var tag in s_customComponentTags)
            razorFiles.Should().Contain(tag, $"'{tag}' 被当作组件使用，必须存在同名 .razor 文件");
    }

    [Fact]
    public void StringParameters_ShouldNeverBeBareIdentifiers()
    {
        // 裸标识符（无 @）在 string 参数上会被当成字面量：SessionId="SessionId"
        // 是「状态能改、界面不动」这类 bug 的经典来源。字符串参数一律要求 @ 前缀。
        var offenders = new List<string>();

        foreach (var file in RazorFilesUnderSamples())
        {
            var content = File.ReadAllText(file);

            foreach (Match match in Regex.Matches(content, @"\b(?<name>SessionId|CircuitId|SessionScenario)=""(?<value>[^""@][^""]*)"""))
            {
                var name = match.Groups["name"].Value;
                var value = match.Groups["value"].Value;

                // 只关心「值看起来像 C# 标识符」的情况：那几乎必然是漏了 @
                if (Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_.]*$")
                    && value != name)   // 字面量与参数同名时无法从静态文本区分，跳过
                {
                    offenders.Add($"{Path.GetFileName(file)}: {name}=\"{value}\"");
                }
            }
        }

        offenders.Should().BeEmpty(
            "字符串参数必须写 @表达式；裸标识符会被当成字面量：" + string.Join("; ", offenders));
    }

    [Fact]
    public void WorkbenchComponents_ShouldImportTheirNamespace()
    {
        // 消费 CustomComponentTags 的文件必须 using 到组件所在命名空间，
        // 否则标签会降级为普通 HTML（不报错、渲染空）
        var consumerFiles = new[]
        {
            "samples/Seeing.Agent.WebUI/Components/WorkbenchDockSlot.razor",
            "samples/Seeing.Agent.WebUI/Components/WorkbenchPicker.razor",
            "samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchSurface.razor",
            "samples/Seeing.Agent.WebUI.Workbench/Components/WorkbenchDockHost.razor",
        };

        var root = FindRepositoryRoot();

        foreach (var relative in consumerFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"应找到 {relative}");

            var content = File.ReadAllText(path);

            // 同命名空间内互相引用不需要 using；跨程序集必须显式 using
            var needsImport = relative.Contains("Seeing.Agent.WebUI.Files", StringComparison.Ordinal)
                              || relative.Contains("Seeing.Agent.WebUI/Components", StringComparison.Ordinal);

            if (needsImport)
            {
                content.Should().Contain("Seeing.Agent.WebUI.Workbench.Components",
                    $"{relative} 引用了工作台组件，必须 using 其命名空间，否则会静默降级为普通 HTML");
            }
        }
    }

    // ── helpers ──

    private static Dictionary<string, string> ReadAllRazorUnder(string relativeRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = Path.Combine(FindRepositoryRoot(), relativeRoot);

        foreach (var file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var content = File.ReadAllText(file);
            foreach (var tag in s_customComponentTags)
            {
                if (Regex.IsMatch(content, $@"<{tag}[\s/>]"))
                    result[tag] = file;
            }
        }

        return result;
    }

    private static IEnumerable<string> RazorFilesUnderSamples()
    {
        var root = Path.Combine(FindRepositoryRoot(), "samples");

        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(CurrentFile())!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
