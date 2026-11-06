using FluentAssertions;
using System.Text.RegularExpressions;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Hosting;

/// <summary>
/// 守卫「桌面宿主发布」的项目配置。
/// <para>
/// 现场症状：<c>dotnet publish samples/Seeing.Agent.Desktop</c> 或 VS 发布时报
/// <c>UpdateExternallyDefinedStaticWebAssets 任务意外失败</c>，
/// 或产物里混进 linux-x64 的静态资源清单。
/// </para>
/// <para>
/// 根因：<c>PublishProfile</c> / <c>PublishUrl</c> 是 **MSBuild 全局属性**，
/// 会随 <c>ProjectReference</c> 传播给被引用项目。Desktop 与 WebUI 恰好各有
/// 一个名为 <c>FolderProfile.pubxml</c> 的发布配置，内容却指向不同 RID
/// （桌面 win-x64、WebUI linux-x64）。于是从桌面发布时，WebUI 会套用**自己**
/// 那份 linux-x64 profile，被按 linux-x64 构建/发布。
/// </para>
/// <para>
/// 这类错误无法被常规单元测试发现（它们是 MSBuild 属性传播问题），
/// 因此按项目文件源码断言。发布配置只属于声明它的那个项目，不该下传。
/// </para>
/// </summary>
public class DesktopPublishProfileIsolationTests
{
    /// <summary>
    /// 桌面宿主引用 WebUI 时必须剥离发布配置属性，否则 WebUI 会套用自己的
    /// 同名 FolderProfile（linux-x64），把发布目标带偏。
    /// </summary>
    [Fact]
    public void DesktopProjectReference_ShouldRemovePublishProfileProperties()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj");

        // 找到指向 WebUI 的那个 ProjectReference 节点整体
        var match = Regex.Match(
            csproj,
            @"<ProjectReference[^>]*Seeing\.Agent\.WebUI\.csproj[^>]*>(.*?)</ProjectReference>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        match.Success.Should().BeTrue("桌面宿主必须引用 WebUI（复用宿主组装与静态资源）");

        var node = match.Value;

        node.Should().Contain("GlobalPropertiesToRemove",
            "PublishProfile 是全局属性，会传播给被引用项目；" +
            "Desktop 与 WebUI 的 FolderProfile 指向不同 RID，不剥离就会按错 RID 发布");

        Regex.IsMatch(node, @"GlobalPropertiesToRemove>[^<]*PublishProfile", RegexOptions.CultureInvariant)
            .Should().BeTrue("至少要剥离 PublishProfile，它是把 WebUI 带向 linux-x64 的直接原因");
    }

    /// <summary>
    /// 嵌套 MSBuild 调用 ChannelHost 时同样要剥离发布配置：
    /// ChannelHost 没有发布配置，套用外层名称只会得到
    /// <c>NETSDK1198: 在项目中找不到名为 FolderProfile 的发布配置文件</c>。
    /// </summary>
    [Fact]
    public void ChannelHostNestedPublish_ShouldRemovePublishProfileProperties()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

        var msbuildCalls = Regex.Matches(
            csproj,
            @"<MSBuild[^>]*Seeing\.Gateway\.ChannelHost\.csproj[^>]*/?>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        msbuildCalls.Should().NotBeEmpty("WebUI 需要把 ChannelHost 发布到输出/发布目录");

        foreach (Match call in msbuildCalls)
        {
            call.Value.Should().Contain("RemoveProperties",
                "嵌套 Publish 必须剔除 PublishProfile —— ChannelHost 没有该配置，" +
                "套用外层名称会报 NETSDK1198 甚至把 RID 带偏");
        }
    }

    /// <summary>
    /// ChannelHost 完整产物必须落到输出目录。
    /// <para>
    /// WebUI 以 <c>ReferenceOutputAssembly="false"</c> 引用 ChannelHost，该设置下
    /// SDK 只复制 apphost(.exe)、deps.json、runtimeconfig.json，**不复制 .dll**。
    /// 运行期 <c>ChannelHostManager.ResolveChannelHost</c> 要求 .dll 存在，
    /// 缺了会抛「找不到可运行的 Seeing.Gateway.ChannelHost」。
    /// </para>
    /// <para>
    /// 历史上这个补齐目标的 Condition 写成 <c>'$(PublishDir)' == ''</c>，
    /// 但 PublishDir 有 SDK 默认值（bin/&lt;config&gt;/&lt;tfm&gt;/&lt;rid&gt;/publish/）永远非空，
    /// 于是目标从不执行 → 输出目录永远缺 .dll。此处钉住「不得再用该条件」。
    /// </para>
    /// </summary>
    [Fact]
    public void ChannelHostBuildTarget_ShouldNotGateOnPublishDir()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

        var target = Regex.Match(
            csproj,
            @"<Target\s+Name=""CopyChannelHostForBuild""(.*?)</Target>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        target.Success.Should().BeTrue("WebUI 必须在构建后补齐 ChannelHost 的完整产物");

        var header = Regex.Match(target.Value, @"<Target[^>]*>", RegexOptions.CultureInvariant).Value;

        header.Should().NotContain("'$(PublishDir)' == ''",
            "PublishDir 有 SDK 默认值永远非空，用它判断构建阶段会让目标永不执行，导致缺 .dll");

        // 嵌套 Publish 的目录必须转绝对路径：
        // 相对路径会以被调用项目目录为基准解析，产物落回 ChannelHost 自己的 bin。
        target.Value.Should().Contain("_ChannelHostBuildPublishDir",
            "嵌套 MSBuild 的 PublishDir 必须是绝对路径，否则产物落回被调用项目的 bin");
    }

    /// <summary>
    /// 桌面发布的 publish 目录同样必须带 ChannelHost 的 .dll。
    /// </summary>
    [Fact]
    public void DesktopPublish_ShouldCarryChannelHostDll()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj");

        csproj.Should().Contain("CopyChannelHostToPublish",
            "Desktop 把 WebUI 产物复制到 publish 时同样只会带 .exe + json，" +
            "必须显式补 .dll，否则桌面端无法启动 ChannelHost");

        Regex.IsMatch(csproj, @"Seeing\.Gateway\.ChannelHost\.\*", RegexOptions.CultureInvariant)
            .Should().BeTrue("补齐时应匹配 Seeing.Gateway.ChannelHost.* 全量文件（含 .dll）");
    }

    /// <summary>
    /// 从测试输出目录回溯到仓库根，读取仓库内文件。
    /// </summary>
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到仓库文件：{relativePath}");
    }
}
