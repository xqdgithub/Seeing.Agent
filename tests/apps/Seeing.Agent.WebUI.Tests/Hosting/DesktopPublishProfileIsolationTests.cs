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
        var targets = ReadRepoFile("build/Seeing.Publish.targets");

        targets.Should().Contain("PublishProfile",
            "共享契约必须剥离 PublishProfile —— ChannelHost 没有该配置，" +
            "套用外层名称会报 NETSDK1198 甚至把 RID 带偏");
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

        // 绝对路径换算由共享契约在各自 target 体内完成（见 build/Seeing.Publish.targets），
        // 项目文件只负责把控制权交给契约，不得自己再换算一遍。
        target.Value.Should().Contain("_SeeingChannelHostForBuild",
            "构建期补产物必须交给契约的专用 target，避免各写一套");

        ReadRepoFile("build/Seeing.Publish.targets").Should().Contain("IsPathRooted",
            "绝对路径换算必须存在于契约内（相对路径以被调用项目目录为基准解析）");
    }

    /// <summary>
    /// 发布 ChannelHost 时必须同时覆盖 $(PublishIntermediateOutputPath)。
    /// <para>
    /// 现场症状：VS 用 WebUI 的 FolderProfile（linux-x64）发布后，
    /// <c>Z:\Seeing-Agent\publish</c> 里只有 <c>Seeing.Gateway.ChannelHost.deps.json</c> 与
    /// <c>.runtimeconfig.json</c>，**没有 .dll**，运行期 ResolveChannelHost 直接失败。
    /// </para>
    /// <para>
    /// 根因：FileSystem / VS 发布（<c>DeployOnBuild=true</c>）会把
    /// <c>_PrepareForAspNetCoreProjectSystemPublish</c> 里的 <c>PublishDir</c> 指向
    /// <c>obj/&lt;Config&gt;/&lt;tfm&gt;/&lt;rid&gt;/PubTmp/Out/</c>，
    /// 而 <c>FileSystemPublish</c> 只从 <c>$(PublishIntermediateOutputPath)**\*.*</c> 收集文件。
    /// 只写 <c>$(PublishDir)</c> 时 .dll 落在收集目录之外，发布产物就只剩
    /// ProjectReference（<c>ReferenceOutputAssembly=false</c>）带过去的「半套」文件。
    /// </para>
    /// </summary>
    [Fact]
    public void ChannelHostPublishTarget_ShouldAlsoCoverPublishIntermediateOutputPath()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

        var target = Regex.Match(
            csproj,
            @"<Target\s+Name=""PublishChannelHost""(.*?)</Target>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        target.Success.Should().BeTrue("WebUI 必须在发布后补齐 ChannelHost 的完整产物");

        target.Value.Should().Contain("PublishIntermediateOutputPath",
            "FileSystem/VS 发布只从 $(PublishIntermediateOutputPath)（obj/.../PubTmp/Out）收集文件，" +
            "只写 $(PublishDir) 会把 .dll 漏在收集目录之外");
    }

    /// <summary>
    /// 发布阶段用到的目录属性必须在使用它的 target 体内计算。
    /// <para>
    /// MSBuild 的 target 在一次构建里只执行一次。历史上目录属性算在独立的
    /// <c>PassChannelHostPublishDirs</c> target 里，被 <c>CopyChannelHostForBuild</c>
    /// 依赖后在 Build 阶段就求值并冻结为 <c>bin/&lt;Config&gt;/&lt;tfm&gt;/&lt;rid&gt;/publish/</c>；
    /// 到 Publish 阶段 <c>$(PublishDir)</c> 已变成 <c>PubTmp\Out</c>，
    /// 但该 target 被「以前的生成已成功」跳过，属性不会重算 ——
    /// 于是嵌套发布仍然写回旧目录，最终发布目录缺 .dll。
    /// </para>
    /// </summary>
    [Fact]
    public void ChannelHostPublishTarget_ShouldComputeDirsInsideTarget()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

        var target = Regex.Match(
            csproj,
            @"<Target\s+Name=""PublishChannelHost""(.*?)</Target>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        target.Success.Should().BeTrue("WebUI 必须在发布后补齐 ChannelHost 的完整产物");

        var header = Regex.Match(target.Value, @"<Target[^>]*>", RegexOptions.CultureInvariant).Value;

        header.Should().NotContain("PassChannelHostPublishDirs",
            "目录属性不能放在只执行一次的独立 target 里，否则 Build 阶段求值后就冻结，" +
            "Publish 阶段拿到的是旧目录");

        target.Value.Should().Contain("<PropertyGroup>",
            "目录必须在本 target 体内计算，才能拿到发布阶段的实际值");
    }

    /// <summary>
    /// 四个项目必须共用同一份发布契约，而不是各写一套嵌套发布逻辑。
    /// </summary>
    [Fact]
    public void HostProjects_ShouldImportSharedPublishContract()
    {
        foreach (var project in new[]
        {
            "samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj",
            "samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj",
            "samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj",
        })
        {
            ReadRepoFile(project).Should().Contain("Seeing.Publish.targets",
                $"{project} 必须 Import 共享发布契约，否则又回到各写一套");
        }
    }

    /// <summary>
    /// WebUI 不得再自带嵌套发布实现（已收编进契约）。
    /// </summary>
    [Fact]
    public void WebUI_ShouldNotOwnNestedPublishImplementation()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

        csproj.Should().NotContain("<MSBuild Projects=\"$(MSBuildThisFileDirectory)../Seeing.Gateway.ChannelHost",
            "嵌套 MSBuild 调用必须收敛到 Seeing.Publish.targets，项目文件里不得再直接写");
    }

    /// <summary>
    /// ChannelHost 是 RID 无关产物：嵌套发布必须剥离 RuntimeIdentifier，
    /// 否则会去找不存在的 apphost（NETSDK1047 / 找不到 apphost）。
    /// </summary>
    [Fact]
    public void PublishContract_ShouldStripRuntimeIdentifierForChannelHost()
    {
        var targets = ReadRepoFile("build/Seeing.Publish.targets");

        // 两处 ChannelHost 嵌套发布（Build 期 / Publish 期）都必须剥离 RID
        var msbuildCalls = Regex.Matches(
            targets,
            @"<MSBuild[^>]*Seeing\.Gateway\.ChannelHost\.csproj[^>]*/>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        msbuildCalls.Should().NotBeEmpty("契约必须负责把 ChannelHost 发布到宿主目录");

        foreach (Match call in msbuildCalls)
        {
            Regex.IsMatch(call.Value, @"RemoveProperties=""[^""]*RuntimeIdentifier", RegexOptions.CultureInvariant)
                .Should().BeTrue(
                    "ChannelHost 是 RID 无关产物（UseAppHost=false），" +
                    "透传 RID 会让它去找不存在的 apphost / deps.json");
        }
    }

    /// <summary>
    /// Cli 发布 WebUI/Tui 是跨进程打包：必须显式传 RID，
    /// 否则被发布项目按"当前机器"出 apphost，导致 linux 包里混进 Windows .exe。
    /// </summary>
    [Fact]
    public void CliNestedPublish_ShouldPassRuntimeIdentifier()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj");

        var execs = Regex.Matches(
            csproj,
            @"<Exec[^>]*dotnet publish[^>]*>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        execs.Should().NotBeEmpty("Cli 需要嵌套发布 WebUI / Tui");

        // 每条 Exec 要么带 -r $(RuntimeIdentifier)，要么是「未指定 RID」的显式兜底分支。
        // 兜底分支必须自带说明，否则就是悄悄产出错平台产物。
        foreach (Match exec in execs)
        {
            var hasRid = Regex.IsMatch(exec.Value, @"-r\s+\$\(RuntimeIdentifier\)", RegexOptions.CultureInvariant);
            var isNoRidFallback = Regex.IsMatch(
                exec.Value, @"Condition=""'\$\(RuntimeIdentifier\)' == ''""", RegexOptions.CultureInvariant);

            (hasRid || isNoRidFallback).Should().BeTrue(
                "每条嵌套发布要么显式传 -r $(RuntimeIdentifier)，" +
                "要么是声明清楚的「未指定 RID」兜底分支；" +
                "否则跨平台包内会混入错误平台的 apphost");
        }

        // 至少有一条真正传 RID 的分支
        execs.Cast<Match>().Any(e => Regex.IsMatch(e.Value, @"-r\s+\$\(RuntimeIdentifier\)", RegexOptions.CultureInvariant))
            .Should().BeTrue("必须存在带 RID 的发布分支，这是跨平台发布的正常路径");
    }

    /// <summary>
    /// Desktop 使用 Photino + WebView2，只能在 Windows 运行。
    /// 对非 Windows RID 发布时必须明确报错，而不是静默产出坏包。
    /// </summary>
    [Fact]
    public void Desktop_ShouldGuardAgainstNonWindowsRuntimeIdentifier()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj");

        csproj.Should().Contain("StartsWith('win')",
            "Desktop 必须校验 RID 前缀为 win，否则会对 linux/mac 发布出不可运行的包");
    }

    /// <summary>
    /// 无 RID 的 WebUI 发布缺原生库，产物不可用（实测）。
    /// 发布时必须给出明确诊断，不能静默产出坏包。
    /// </summary>
    [Fact]
    public void WebUIPublish_ShouldRequireRuntimeIdentifier()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

        csproj.Should().Contain("_SeeingValidateRuntimeIdentifier",
            "WebUI 发布前必须校验 RuntimeIdentifier，否则会静默产出缺少原生库的包");
    }

    /// <summary>
    /// 发布配置（*.pubxml）被 .gitignore 排除，是每台开发机的本地设置，不入库。
    /// 因此**不得**写依赖其内容或存在的测试 —— 在别的机器 / CI 上文件根本不存在。
    /// <para>
    /// 真正需要守住的不变量放在项目文件里：WebUI 的校验（Task 5）保证无 RID 发布
    /// 会给出明确诊断，Desktop 的守卫（Task 4）保证非 Windows RID 被拦下。
    /// 这两者都在版本控制内，才是可靠的守门点。
    /// </para>
    /// </summary>
    [Fact]
    public void PublishProfileFiles_ShouldNotBeVersionControlled()
    {
        ReadRepoFile(".gitignore").Should().Contain("*.pubxml",
            "发布配置是本地设置（PublishUrl / RID 因机器而异），不入库；" +
            "否则会互相覆盖。因此守门测试不得依赖 pubxml 的内容或存在性。");
    }

    /// <summary>
    /// Desktop 内嵌 WebUI 时不产出 WebUI 独立运行包，且拿不到 Desktop 的 RID，
    /// 必须显式关掉 WebUI 的无-RID 校验，否则桌面端发布会稳定失败（实测回归）。
    /// </summary>
    [Fact]
    public void DesktopProjectReference_ShouldDisableWebUIRuntimeIdentifierValidation()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj");

        var node = Regex.Match(
            csproj,
            @"<ProjectReference[^>]*Seeing\.Agent\.WebUI\.csproj[^>]*>(.*?)</ProjectReference>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        node.Success.Should().BeTrue("桌面宿主必须引用 WebUI");

        node.Value.Should().Contain("SeeingSkipRuntimeIdentifierValidation",
            "从 Desktop 发布时 WebUI 拿不到 RID（实测为空），" +
            "不关掉校验会让桌面端发布直接失败");

        ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj").Should()
            .Contain("SeeingSkipRuntimeIdentifierValidation",
                "WebUI 的校验必须支持被上层显式关闭");
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
    /// ChannelHost 必须保持 RID 无关（不产原生 apphost）。
    /// <para>
    /// 现场症状：从 VS 用 WebUI 的 <c>FolderProfile</c>（linux-x64）发布时报
    /// <c>无法复制 …obj\Release\net10.0\linux-x64\apphost，原因是找不到该文件</c>，
    /// 同时缺 <c>deps.json</c> / <c>runtimeconfig.json</c>。
    /// </para>
    /// <para>
    /// 根因：RID 只写在 WebUI 自己的 PublishProfile 里，不会进入 ChannelHost 的
    /// restore，于是它的 assets 只有 <c>net10.0</c>；但构建期 ProjectReference
    /// 与嵌套 Publish 又会把 RID 带过去，按 <c>net10.0/linux-x64</c> 去取
    /// RID 专属产物（apphost 来自 microsoft.netcore.app.host.&lt;rid&gt; 包）
    /// —— restore 与 build 的 RID 视图不一致。
    /// </para>
    /// <para>
    /// ChannelHost 一律以 <c>dotnet Seeing.Gateway.ChannelHost.dll</c> 启动
    /// （见 <c>ChannelHostManager.BuildChannelHostStartInfo</c>），原生启动器
    /// 从未被使用，因此关掉 apphost 是正确解法，而不是给嵌套调用补 RID。
    /// </para>
    /// </summary>
    [Fact]
    public void ChannelHostProject_ShouldBeRidAgnostic()
    {
        var csproj = ReadRepoFile("samples/Seeing.Gateway.ChannelHost/Seeing.Gateway.ChannelHost.csproj");

        Regex.IsMatch(csproj, @"<UseAppHost>\s*false\s*</UseAppHost>", RegexOptions.CultureInvariant)
            .Should().BeTrue(
                "ChannelHost 只通过 `dotnet xxx.dll` 启动，不该产原生 apphost；" +
                "留着它会把 RID 拉进构建，导致跨 RID 发布时找不到 apphost");
    }

    /// <summary>
    /// 嵌套发布 ChannelHost 时必须剥离 RuntimeIdentifier。
    /// <para>
    /// 一旦透传，MSBuild 会去 <c>obj/&lt;Config&gt;/&lt;tfm&gt;/&lt;rid&gt;/</c> 找
    /// <c>apphost</c> / <c>deps.json</c> / <c>runtimeconfig.json</c>；而该 RID
    /// 目标只存在于入口项目的 assets 里，被调用项目没有 → NETSDK1047 或
    /// 「无法复制 …，原因是找不到该文件」。
    /// </para>
    /// </summary>
    [Fact]
    public void ChannelHostNestedPublish_ShouldNotPropagateRuntimeIdentifier()
    {
        var targets = ReadRepoFile("build/Seeing.Publish.targets");

        // ChannelHost 的 RID 无关性由契约在它自己的嵌套发布里剥离，
        // 调用方不需要、也不应该重复声明。
        Regex.IsMatch(
                targets,
                @"RemoveProperties=""[^""]*RuntimeIdentifier",
                RegexOptions.CultureInvariant)
            .Should().BeTrue(
                "嵌套发布必须剔除 RuntimeIdentifier：ChannelHost 是 RID 无关产物，" +
                "透传 RID 会让它去找不存在的 apphost / deps.json");

        ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj").Should()
            .Contain("_SeeingChannelHostForPublish",
                "WebUI 发布阶段必须交给契约处理，而不是自己写一套嵌套调用");
    }

    /// <summary>
    /// ChannelHost 的 ProjectReference 同样不得传递 RID，
    /// 否则 restore（无 RID）与 build（有 RID）的视图不一致。
    /// </summary>
    [Fact]
    public void ChannelHostProjectReference_ShouldNotPropagateRuntimeIdentifier()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

        var node = Regex.Match(
            csproj,
            @"<ProjectReference[^>]*Seeing\.Gateway\.ChannelHost\.csproj[^>]*>(.*?)</ProjectReference>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        node.Success.Should().BeTrue("WebUI 必须引用 ChannelHost 以保证构建顺序");

        Regex.IsMatch(node.Value, @"GlobalPropertiesToRemove>[^<]*RuntimeIdentifier", RegexOptions.CultureInvariant)
            .Should().BeTrue(
                "ProjectReference 必须剥离 RuntimeIdentifier，" +
                "否则 restore 与 build 的 RID 视图不一致（NETSDK1047）");
    }

    /// <summary>
    /// Desktop 补齐 ChannelHost 时必须同时兼容「RID 无关」与「历史 RID 子目录」两种落点。
    /// <para>
    /// ChannelHost 改成 <c>UseAppHost=false</c> 后落在 <c>bin/&lt;Config&gt;/&lt;tfm&gt;/</c>，
    /// 不再进 <c>&lt;rid&gt;</c> 子目录。若补齐目标仍只认 <c>$(RuntimeIdentifier)</c>，
    /// 桌面端 publish 会静默丢掉这四个文件，运行期 ResolveChannelHost 直接失败。
    /// </para>
    /// </summary>
    [Fact]
    public void DesktopPublish_ShouldFindChannelHostInBothRidAndRidlessDirs()
    {
        var csproj = ReadRepoFile("samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj");

        var target = Regex.Match(
            csproj,
            @"<Target\s+Name=""CopyChannelHostToPublish""(.*?)</Target>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        target.Success.Should().BeTrue("Desktop 必须补齐 ChannelHost 完整产物");

        target.Value.Should().Contain(@"\$(TargetFramework)\Seeing.Gateway.ChannelHost.*",
            "ChannelHost 现在是 RID 无关产物，主来源必须是 bin/<Config>/<tfm>/");

        target.Value.Should().Contain(@"\$(RuntimeIdentifier)\Seeing.Gateway.ChannelHost.*",
            "仍需保留 RID 子目录兜底，兼容外层带 RID 构建的历史布局");
    }

    /// <summary>
    /// 从测试输出目录回溯到仓库根，读取仓库内文件。
    /// </summary>
    private static string ReadRepoFile(string relativePath)
    {
        return File.ReadAllText(ReadRepoFileLocation(relativePath));
    }

    /// <summary>
    /// 从测试输出目录回溯到仓库根，返回仓库内文件的绝对路径。
    /// </summary>
    private static string ReadRepoFileLocation(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到仓库文件：{relativePath}");
    }
}
