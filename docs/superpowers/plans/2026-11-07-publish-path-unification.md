# 发布路径统一 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 WebUI / Desktop / Cli / ChannelHost 四个产物在任一发布入口（VS 发布、`dotnet publish -r`、`dotnet publish -o`、`dotnet build`）下都产出完整、平台正确的包，并把重复的"补文件"逻辑收敛到一份契约里。

**Architecture:** 新建 `build/Seeing.Publish.targets` 作为**唯一**允许写嵌套发布的地方，封装"目录属性在使用点求值""发布目录双写""ChannelHost 剥离 RID""跨进程产物显式传 RID"四个正确姿势。三个宿主项目各自只保留自己的声明，删除各自为战的实现。运维不变式：**WebUI 产物完整 ⇒ 下游宿主完整**。

**Tech Stack:** MSBuild 17 / .NET SDK 10.0.403、.NET 10、xUnit + FluentAssertions（项目内已有）

**Spec:** `docs/superpowers/specs/2026-11-07-publish-path-unification-design.md`

## Global Constraints

- 目标框架 `net10.0`；SDK 10.0.403。
- **RID 是发布必需品**：无 RID 的 WebUI 发布缺 `libe_sqlite3.so`/`.dll`，产物不可用（实测）。
- **`Seeing.Gateway.ChannelHost` 永久 RID 无关**：`UseAppHost=false`，只产 `.dll`/`.deps.json`/`.runtimeconfig.json`，一律 `dotnet *.dll` 启动。任何嵌套调用必须 `RemoveProperties` 掉 `RuntimeIdentifier`。
- **`.pubxml` 不得写死 `RuntimeIdentifier`**（发布配置只属于声明它的项目；平台由外层决定）。
- 嵌套发布的目录参数**必须绝对路径**（相对路径以被调用项目目录为基准解析）。
- **不得透传 `BaseIntermediateOutputPath`** 到嵌套调用（实测导致 `project.assets.json` / restore 视图错位）。
- 命令行传 `-p:RuntimeIdentifier` **必须配 `-restore`**，否则 `NETSDK1047`（用法约束，非缺陷）。
- 注释一律中文，与仓库现有风格一致。
- 测试用 VSTest 跑已构建程序集（MTP 在本环境无法发现测试）：
  `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Release/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~<Class>"`

## Review Focus

- **无 RID 发布**：用户执行 `dotnet publish -o <dir>` 而不带 `-r`。期望：给出明确诊断或产出自洽的可移植包，**不能**静默产出缺原生库的坏包。（Task 5）
- **Cli 跨平台**：在 Linux 上发布 `-r linux-x64`。期望：包内 `Seeing.Agent.WebUI`、`seeing-tui` 为 Linux 可执行文件，**不得**出现 `.exe`。（Task 3）
- **外部 `-o` 与 pubxml `DeleteExistingFiles`**：外层指定外部目录且 pubxml 要求删旧文件。期望：只清目标目录，不误删仓库内 `bin`。（Task 6）
- **Desktop 非 Windows RID**：用户对 Desktop 指定 `-r linux-x64`。期望：明确报错说明 Desktop 仅支持 Windows，而非静默产出坏包。（Task 4）
- **ChannelHost 被 RID 污染**：任一入口带 RID 发布。期望：ChannelHost 产物仍为 RID 无关，不出现 `apphost`/`.exe`。（Task 2）

---

## File Structure

| 文件 | 职责 |
|---|---|
| `build/Seeing.Publish.targets`（新建） | 唯一嵌套发布出口：`_SeeingNestedPublish`、`SeeingCopyPublishedFiles`、RID 校验 |
| `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj` | 声明依赖 ChannelHost；构建期同步到输出目录 |
| `samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj` | 声明依赖 WebUI（同 RID）；Windows-only 校验 |
| `samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj` | 嵌套发布 WebUI/Tui，**显式传 RID** |
| `samples/Seeing.Agent.WebUI/Properties/PublishProfiles/*.pubxml` | 去掉 RID；重命名 |
| `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs` | 现有守门测试，扩展 |

> **已完成的前置改动**（本次工作开始时由排查产生，已在工作区）：
> `Seeing.Agent.WebUI.csproj` 的 F1 修复（发布目录双写、属性在使用点求值）与 2 个新守门测试。
> Task 1 负责把它收编进契约文件。

---

### Task 1: 建立发布契约并迁移 WebUI 的 ChannelHost 逻辑

**Files:**
- Create: `build/Seeing.Publish.targets`
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj:84-230`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs`

**Interfaces:**
- Produces:
  - `_SeeingNestedPublish` —— target，输入**普通字符串属性**（不用元数据批处理，那在 `RemoveProperties`/`Properties` 属性位置不生效）：
    - `$(SeeingNestedProject)` 项目路径
    - `$(SeeingNestedTargets)` 默认 `Publish`
    - `$(SeeingNestedPublishDir)` 主落点
    - `$(SeeingNestedStagingDir)` 副落点（可空）
    - `$(SeeingNestedExtraProperties)` 追加属性串，如 `SelfContained=false`
    - `$(SeeingNestedExtraRemoveProperties)` 追加剥离串，如 `RuntimeIdentifier`
    把产物写到 `$(SeeingNestedPublishDir)` 与 `$(SeeingNestedStagingDir)`（去重后各写一次）。
  - `SeeingCopyPublishedFiles` —— target，输入 `$(SeeingCopySourceDir)`、`$(SeeingCopyDestinationDir)`、`$(SeeingCopyManifestPath)`；带 manifest 清理。
  - 属性 `SeeingRequireRuntimeIdentifier`（默认 `true`）：为 `true` 且 `$(RuntimeIdentifier)` 为空时产生明确诊断。

> **实现前必读（来自实测日志，决定成败）**
>
> `PublishChannelHost` 执行时 **`$(PublishDir)` 仍是 `bin/<config>/<tfm>/<rid>/publish/`**，
> 而 SDK 稍后才在 `_PrepareForAspNetCoreProjectSystemPublish` 里把它改写成 `obj/.../PubTmp/Out/`。
> 同时 `$(PublishIntermediateOutputPath)` 的**默认值**恰好就是 `obj/<config>/<tfm>/<rid>/PubTmp/Out/`，
> 这才是 `FileSystemPublish` 的真实收集源。
>
> 因此修复之所以成立，靠的是**副落点写进 `PublishIntermediateOutputPath` 的默认值**，
> 不是靠读取某个晚设的属性。实现时若把副落点改成其它变量，F1 会立即回归。

- [ ] **Step 1: 写失败测试**

在 `DesktopPublishProfileIsolationTests.cs` 末尾追加：

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests -c Release && dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Release/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~DesktopPublishProfileIsolationTests"`
Expected: 2 个新测试 FAIL（`Seeing.Publish.targets` 尚不存在 / 项目文件仍有直接 MSBuild 调用）

- [ ] **Step 3: 创建 `build/Seeing.Publish.targets`**

```xml
<Project>
  <!--
    Seeing.Agent 宿主族发布契约（唯一嵌套发布出口）

    背景：WebUI / Desktop / Cli 都需要把别的项目的发布产物装进自己目录，
    历史上三处各写一套，踩过同一组坑（详见 docs/superpowers/specs/
    2026-11-07-publish-path-unification-design.md）。这里把它固化成一份。

    四条硬约束（每条都对应一次真实故障）：
    1. 目录属性必须在使用它的 target 体内计算 —— target 一次构建只执行一次，
       放进独立 target 会在 Build 阶段求值后冻结。
    2. 发布目录要双写 $(PublishDir) 与 $(PublishIntermediateOutputPath) ——
       VS/FileSystem 发布把后者当作收集源，只写前者会漏文件。
    3. ChannelHost 必须剥离 RuntimeIdentifier —— 它是 RID 无关产物。
    4. 目录参数必须绝对路径 —— 相对路径以被调用项目目录为基准解析。
  -->
  <PropertyGroup>
    <SeeingRequireRuntimeIdentifier Condition="'$(SeeingRequireRuntimeIdentifier)' == ''">true</SeeingRequireRuntimeIdentifier>
  </PropertyGroup>

  <Target Name="_SeeingValidateRuntimeIdentifier">
    <Error Condition="'$(SeeingRequireRuntimeIdentifier)' == 'true' and '$(RuntimeIdentifier)' == ''"
           Text="发布必须指定 RuntimeIdentifier（-r &lt;rid&gt;）。不带 RID 的产物缺少平台原生库（如 libe_sqlite3），无法运行。"
           ContinueOnError="false" />
  </Target>

  <Target Name="_SeeingNestedPublish">
    <PropertyGroup>
      <_SeeingNestedTargets Condition="'$(SeeingNestedTargets)' == ''">Publish</_SeeingNestedTargets>
      <_SeeingNestedSecret Remove="" />

      <_SeeingNestedPublishDirAbs Condition="'$(SeeingNestedPublishDir)' != '' and !$([System.IO.Path]::IsPathRooted('$(SeeingNestedPublishDir)'))">$([System.IO.Path]::GetFullPath('$(MSBuildThisFileDirectory)$(SeeingNestedPublishDir)'))</_SeeingNestedPublishDirAbs>
      <_SeeingNestedPublishDirAbs Condition="'$(SeeingNestedPublishDir)' != '' and $([System.IO.Path]::IsPathRooted('$(SeeingNestedPublishDir)'))">$([MSBuild]::NormalizeDirectory('$(SeeingNestedPublishDir)'))</_SeeingNestedPublishDirAbs>

      <_SeeingNestedStagingDirAbs Condition="'$(SeeingNestedStagingDir)' != '' and !$([System.IO.Path]::IsPathRooted('$(SeeingNestedStagingDir)'))">$([System.IO.Path]::GetFullPath('$(SeeingNestedStagingDir)'))</_SeeingNestedStagingDirAbs>
      <_SeeingNestedStagingDirAbs Condition="'$(SeeingNestedStagingDir)' != '' and $([System.IO.Path]::IsPathRooted('$(SeeingNestedStagingDir)'))">$([MSBuild]::NormalizeDirectory('$(SeeingNestedStagingDir)'))</_SeeingNestedStagingDirAbs>

      <!-- RemoveProperties 与 Properties 都是纯字符串，元数据批处理在此不生效，必须拼串 -->
      <_SeeingNestedRemoveProperties>PublishProfile;PublishUrl;WebPublishMethod</_SeeingNestedRemoveProperties>
      <_SeeingNestedRemoveProperties Condition="'$(SeeingNestedExtraRemoveProperties)' != ''">$(_SeeingNestedRemoveProperties);$(SeeingNestedExtraRemoveProperties)</_SeeingNestedRemoveProperties>

      <_SeeingNestedProperties>Configuration=$(Configuration);PublishDir=$(_SeeingNestedPublishDirAbs)</_SeeingNestedProperties>
      <_SeeingNestedProperties Condition="'$(SeeingNestedExtraProperties)' != ''">$(_SeeingNestedProperties);$(SeeingNestedExtraProperties)</_SeeingNestedProperties>
    </PropertyGroup>

    <!-- 主落点 -->
    <MSBuild Projects="$(SeeingNestedProject)"
             Targets="$(_SeeingNestedTargets)"
             RemoveProperties="$(_SeeingNestedRemoveProperties)"
             Properties="$(_SeeingNestedProperties)" />

    <!--
      副落点：VS/FileSystem 发布的收集源。
      用复制而非再发布一次，避免同一项目并行发布争抢中间文件。
    -->
    <ItemGroup Condition="'$(_SeeingNestedStagingDirAbs)' != '' and '$(_SeeingNestedStagingDirAbs)' != '$(_SeeingNestedPublishDirAbs)'">
      <_SeeingNestedArtifact Include="$(_SeeingNestedPublishDirAbs)**\*.*" />
    </ItemGroup>

    <Copy SourceFiles="@(_SeeingNestedArtifact)"
          DestinationFiles="@(_SeeingNestedArtifact->'$(_SeeingNestedStagingDirAbs)%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true"
          Condition="'@(_SeeingNestedArtifact)' != ''" />
  </Target>

  <Target Name="SeeingCopyPublishedFiles">
    <ReadLinesFromFile File="$(SeeingCopyManifestPath)" Condition="Exists('$(SeeingCopyManifestPath)')">
      <Output TaskParameter="Lines" ItemName="_SeeingPreviousFiles" />
    </ReadLinesFromFile>

    <ItemGroup>
      <_SeeingCopySource Include="$(SeeingCopySourceDir)**\*" />
      <_SeeingCopySource Remove="@(_SeeingCopySource)" Condition="!Exists('$(SeeingCopySourceDir)')" />
    </ItemGroup>

    <Delete Files="@(_SeeingPreviousFiles->'$(SeeingCopyDestinationDir)%(Identity)')"
            Condition="@(_SeeingPreviousFiles) != ''" />

    <Copy SourceFiles="@(_SeeingCopySource)"
          DestinationFiles="@(_SeeingCopySource->'$(SeeingCopyDestinationDir)%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="false"
          OverwriteReadOnlyFiles="true" />

    <WriteLinesToFile File="$(SeeingCopyManifestPath)"
                      Lines="@(_SeeingCopySource->'%(RecursiveDir)%(Filename)%(Extension)')"
                      Overwrite="true"
                      Encoding="UTF-8" />
  </Target>
</Project>
```

- [ ] **Step 4: 改造 WebUI 项目文件**

`Seeing.Agent.WebUI.csproj`：在 `</Project>` 前 Import 契约（`<Import Project="$(MSBuildThisFileDirectory)..\..\build\Seeing.Publish.targets" />`）；
把两个 target 改为调用契约（**删除**所有 `<MSBuild Projects=...ChannelHost...>` 直接调用）。

`CopyChannelHostForBuild` / `PublishChannelHost` 替换为：

```xml
  <Target Name="CopyChannelHostForBuild" AfterTargets="Build">
    <PropertyGroup>
      <SeeingNestedProject>$(MSBuildThisFileDirectory)..\Seeing.Gateway.ChannelHost\Seeing.Gateway.ChannelHost.csproj</SeeingNestedProject>
      <SeeingNestedPublishDir>$(OutDir)</SeeingNestedPublishDir>
      <SeeingNestedStagingDir></SeeingNestedStagingDir>
      <SeeingNestedExtraProperties>SelfContained=false</SeeingNestedExtraProperties>
      <SeeingNestedExtraRemoveProperties>RuntimeIdentifier</SeeingNestedExtraRemoveProperties>
    </PropertyGroup>
    <CallTarget Targets="_SeeingNestedPublish" />
    <Delete Files="$(OutDir)Seeing.Gateway.ChannelHost" Condition="Exists('$(OutDir)Seeing.Gateway.ChannelHost')" />
    <RemoveDir Directories="$(OutDir)channel-host" Condition="Exists('$(OutDir)channel-host')" />
  </Target>

  <Target Name="PublishChannelHost" AfterTargets="Publish;PublishOnly">
    <PropertyGroup>
      <SeeingNestedProject>$(MSBuildThisFileDirectory)..\Seeing.Gateway.ChannelHost\Seeing.Gateway.ChannelHost.csproj</SeeingNestedProject>
      <SeeingNestedPublishDir>$(PublishDir)</SeeingNestedPublishDir>
      <!-- 副落点必须是 PublishIntermediateOutputPath：这是 FileSystemPublish 的真实收集源 -->
      <SeeingNestedStagingDir>$(PublishIntermediateOutputPath)</SeeingNestedStagingDir>
      <SeeingNestedExtraProperties>SelfContained=false</SeeingNestedExtraProperties>
      <SeeingNestedExtraRemoveProperties>RuntimeIdentifier</SeeingNestedExtraRemoveProperties>
    </PropertyGroup>
    <CallTarget Targets="_SeeingNestedPublish" />
    <Delete Files="$(PublishDir)Seeing.Gateway.ChannelHost" Condition="Exists('$(PublishDir)Seeing.Gateway.ChannelHost')" />
  </Target>
```

- [ ] **Step 5: 运行守门测试**

Run: `dotnet build tests/apps/Seeing.Agent.WebUI.Tests -c Release && dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Release/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~DesktopPublishProfileIsolationTests"`
Expected: 全部 PASS（含原有 8 个 + 新增 2 个）

- [ ] **Step 6: 端到端验证 F1 不回归**

```powershell
$o='C:\Users\quand\AppData\Local\Temp\opencode\t1'; Remove-Item $o -Recurse -Force -EA 0
dotnet msbuild samples\Seeing.Agent.WebUI\Seeing.Agent.WebUI.csproj -restore -t:Build `
  -p:Configuration=Release -p:PublishProfile=FolderProfile1 -p:DeployOnBuild=true -p:PublishUrl="$o" -v:m
Get-ChildItem $o -Filter '*ChannelHost*' | Select-Object Name
```
Expected: 四件套齐全（`.dll` / `.deps.json` / `.runtimeconfig.json` / `.pdb`）

- [ ] **Step 7: 提交**

```bash
git add build/Seeing.Publish.targets samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs docs/superpowers/specs/2026-11-07-publish-path-unification-design.md
git commit -m "refactor(publish): 建立宿主族发布契约，WebUI 的 ChannelHost 逻辑收编进契约"
```

---

### Task 2: 验证 ChannelHost 的 RID 无关性抗污染

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`（仅在测试暴露问题时）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `_SeeingNestedPublish` + `SeeingNestedExtraRemoveProperties`

- [ ] **Step 1: 写失败测试（断言契约里 ChannelHost 的调用必须剥离 RID）**

```csharp
/// <summary>
/// ChannelHost 是 RID 无关产物：任何嵌套发布都必须剥离 RuntimeIdentifier，
/// 否则会去找不存在的 apphost（NETSDK1047 / 找不到 apphost）。
/// </summary>
[Fact]
public void PublishContract_ShouldStripRuntimeIdentifierForChannelHost()
{
    var targets = ReadRepoFile("build/Seeing.Publish.targets");

    targets.Should().Contain("RemoveProperties",
        "契约必须提供剥离属性的通道，供 ChannelHost 这类 RID 无关产物使用");

    var webui = ReadRepoFile("samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj");

    Regex.Matches(webui, @"<SeeingNestedExtraRemoveProperties[^>]*>", RegexOptions.CultureInvariant)
        .Cast<Match>()
        .Should().NotBeEmpty("WebUI 调用契约时必须声明剥离 RuntimeIdentifier");

    foreach (Match m in Regex.Matches(webui, @"<SeeingNestedExtraRemoveProperties[^>]*>", RegexOptions.CultureInvariant))
    {
        m.Value.Should().Contain("RuntimeIdentifier",
            "ChannelHost 是 RID 无关产物，嵌套发布必须剥离 RID");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Release/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~PublishContract_ShouldStripRuntimeIdentifierForChannelHost"`
Expected: 若 Task 1 已正确实现则 PASS；若断言不满足则 FAIL（据实修正）

- [ ] **Step 3: 实测 ChannelHost 产物对 RID 不敏感**

```powershell
$o='C:\Users\quand\AppData\Local\Temp\opencode\t2'; Remove-Item $o -Recurse -Force -EA 0
dotnet publish samples\Seeing.Agent.WebUI\Seeing.Agent.WebUI.csproj -c Release -r linux-x64 -o $o -v:q
# 期望：只有 4 个文件，且没有 apphost（无扩展名可执行文件）
Get-ChildItem $o -Filter 'Seeing.Gateway.ChannelHost*' | Select-Object Name,Length
```
Expected: 恰好 `.dll` / `.deps.json` / `.runtimeconfig.json` / `.pdb`，**没有** `Seeing.Gateway.ChannelHost`（无扩展名 apphost）

- [ ] **Step 4: 提交**

```bash
git add tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs
git commit -m "test(publish): 钉住 ChannelHost 的 RID 无关性"
```

---

### Task 3: 修复 Cli 跨平台发布（F3）

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj:79-153`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SeeingCopyPublishedFiles`
- Produces: `$(WebUiPublishStagingDir)`、`$(TuiPublishStagingDir)` 保持原语义

- [ ] **Step 1: 写失败测试**

```csharp
/// <summary>
/// Cli 发布 WebUI/Tui 是跨进程打包：必须显式传 RID，
/// 否则被发布项目按"当前机器"出 apphost，导致 linux 包里混进 Windows .exe。
/// </summary>
[Fact]
public void CliNestedPublish_ShouldPassRuntimeIdentifier()
{
    var csproj = ReadRepoFile("samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj");

    var execs = Regex.Matches(csproj, @"<Exec[^>]*dotnet publish[^>]*>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    execs.Should().NotBeEmpty("Cli 需要嵌套发布 WebUI / Tui");

    foreach (Match exec in execs)
    {
        Regex.IsMatch(exec.Value, @"-r\s+\$\(RuntimeIdentifier\)", RegexOptions.CultureInvariant)
            .Should().BeTrue(
                "嵌套发布必须显式传 -r $(RuntimeIdentifier)，否则跨平台包内会混入错误平台的 apphost");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~CliNestedPublish_ShouldPassRuntimeIdentifier"`
Expected: FAIL —— 现有两条 `Exec` 均无 `-r`

- [ ] **Step 3: 给两条 Exec 补 RID，并把 staging 拷贝改用契约**

将 `PublishWebUIForCli` / `PublishTuiForCli` 中的 `Exec` 命令改为：

```xml
<Exec Command="set &quot;DOTNET_CLI_UI_LANGUAGE=en&quot; &amp;&amp; dotnet publish &quot;$(MSBuildThisFileDirectory)../Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj&quot; -c $(Configuration) -r $(RuntimeIdentifier) -o &quot;$(WebUIPublishStagingDir)&quot; -p:SelfContained=false --nologo"
      StandardOutputImportance="high"
      Condition="'$(RuntimeIdentifier)' != ''" />
```

Tui 同理（路径换成 `Seeing.Agent.Tui`，目录换成 `$(TuiPublishStagingDir)`）。

`$(RuntimeIdentifier)` 为空时的兜底：保留原命令（不带 `-r`）作为 `Condition="'$(RuntimeIdentifier)' == ''"` 的分支，并在输出中提示"未指定 RID，产出为当前机器平台"。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~CliNestedPublish_ShouldPassRuntimeIdentifier"`
Expected: PASS

- [ ] **Step 5: 实测 linux-x64 包内不出现 .exe**

```powershell
$o='C:\Users\quand\AppData\Local\Temp\opencode\cli-fix'; Remove-Item $o -Recurse -Force -EA 0
dotnet publish samples\Seeing.Agent.Cli\Seeing.Agent.Cli.csproj -c Release -r linux-x64 -o $o -v:q
# 断言：没有任何 .exe
Get-ChildItem $o -Filter '*.exe'
```
Expected: **无输出**（修复前有 `Seeing.Agent.WebUI.exe`、`seeing-tui.exe` 两个 PE 文件）

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs
git commit -m "fix(cli): 嵌套发布 WebUI/Tui 显式传 RID，修复跨平台包混入 Windows apphost"
```

---

### Task 4: Desktop 的 Windows-only 守卫

**Files:**
- Modify: `samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `_SeeingNestedPublish`

- [ ] **Step 1: 写失败测试**

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~Desktop_ShouldGuardAgainstNonWindowsRuntimeIdentifier"`
Expected: FAIL

- [ ] **Step 3: 加入 RID 守卫**

在 `Seeing.Agent.Desktop.csproj` 中加入（并 Import 契约）：

```xml
  <Import Project="$(MSBuildThisFileDirectory)..\..\build\Seeing.Publish.targets" />

  <!--
    Desktop 用 Photino + WebView2，只能在 Windows 运行。
    对非 Windows RID 发布时必须明确报错，而不是产出跑不起来的包。
  -->
  <Target Name="_ValidateDesktopRuntimeIdentifier" BeforeTargets="Build">
    <Error Condition="'$(RuntimeIdentifier)' != '' and !$(RuntimeIdentifier.StartsWith('win'))"
           Text="Seeing.Agent.Desktop 仅支持 Windows（当前 RuntimeIdentifier=$(RuntimeIdentifier)）。请使用 -r win-x64。" />
  </Target>
```

同时把 `CopyChannelHostToPublish` 改为调用契约（保持"从 WebUI bin 拷贝"的既有语义，**不删除**该能力）：

```xml
  <Target Name="CopyChannelHostToPublish" AfterTargets="Publish" Condition="Exists('$(PublishDir)')">
    <PropertyGroup>
      <SeeingCopySourceDir>$(MSBuildThisFileDirectory)..\Seeing.Agent.WebUI\bin\$(Configuration)\$(TargetFramework)\</SeeingCopySourceDir>
      <SeeingCopyDestinationDir>$(PublishDir)</SeeingCopyDestinationDir>
      <SeeingCopyManifestPath>$(PublishDir)\.seeing-channelhost-files.txt</SeeingCopyManifestPath>
    </PropertyGroup>
    <ItemGroup>
      <_ChannelHostFiles Include="$(SeeingCopySourceDir)Seeing.Gateway.ChannelHost.*" />
    </ItemGroup>
    <Copy SourceFiles="@(_ChannelHostFiles)" DestinationFolder="$(PublishDir)" SkipUnchangedFiles="true"
          Condition="'@(_ChannelHostFiles)' != ''" />
  </Target>
```

> **注意**：Desktop 保留自己的补齐动作（实测必需，见 spec §3.2）。契约只是统一实现手法，不是删除能力。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~DesktopPublishProfileIsolationTests"`
Expected: 全部 PASS

- [ ] **Step 5: 实测 Desktop 仍产出完整包**

```powershell
$o='C:\Users\quand\AppData\Local\Temp\opencode\desk-fix'; Remove-Item $o -Recurse -Force -EA 0
dotnet msbuild samples\Seeing.Agent.Desktop\Seeing.Agent.Desktop.csproj -restore -t:Build `
  -p:Configuration=Release -p:PublishProfile=FolderProfile -p:DeployOnBuild=true -p:PublishUrl="$o" -v:m
Get-ChildItem $o -Filter '*ChannelHost*'; Get-ChildItem $o -Filter 'e_sqlite3*'
```
Expected: ChannelHost 四件套 + `e_sqlite3.dll` 均在

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.Desktop/Seeing.Agent.Desktop.csproj tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs
git commit -m "fix(desktop): 增加 Windows-only RID 守卫，补齐逻辑改用发布契约"
```

---

### Task 5: 无 RID 发布时给出明确诊断

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SeeingRequireRuntimeIdentifier` / `_SeeingValidateRuntimeIdentifier`

- [ ] **Step 1: 写失败测试**

```csharp
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
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~WebUIPublish_ShouldRequireRuntimeIdentifier"`
Expected: FAIL

- [ ] **Step 3: 在 PublishChannelHost 前挂校验**

```xml
  <Target Name="_ValidateWebUIPublishRuntimeIdentifier" BeforeTargets="Publish" Condition="'$(IsPublishing)' == 'true' or '$(DeployOnBuild)' == 'true'">
    <CallTarget Targets="_SeeingValidateRuntimeIdentifier" />
  </Target>
```

> 注：`dotnet build` 不带 RID 仍须可用（开发期），所以校验只在发布阶段挂。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~WebUIPublish_ShouldRequireRuntimeIdentifier"`
Expected: PASS

- [ ] **Step 5: 实测诊断文案**

```powershell
dotnet publish samples\Seeing.Agent.WebUI\Seeing.Agent.WebUI.csproj -c Release -o C:\Users\quand\AppData\Local\Temp\opencode\norid
```
Expected: 构建失败且报出"发布必须指定 RuntimeIdentifier（-r <rid>）…缺少平台原生库"。
**若实测发现 VS 默认发布不受影响但 CLI 无 RID 失败，则保留该校验；若导致合法场景（如仅 `dotnet build`）误报，则收窄条件。**

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs
git commit -m "fix(publish): 无 RID 发布时给出明确诊断，避免产出缺原生库的坏包"
```

---

### Task 6: 清理 pubxml 的 RID 并统一命名

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Properties/PublishProfiles/FolderProfile1.pubxml` → 重命名为 `WebUI-linux-x64.pubxml`
- Modify: `samples/Seeing.Agent.Desktop/Properties/PublishProfiles/FolderProfile.pubxml`
- Modify: `samples/Seeing.Agent.Cli/Properties/PublishProfiles/FolderProfile.pubxml`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs`

**Interfaces:**
- Consumes: Task 1–5 全部

- [ ] **Step 1: 写失败测试**

```csharp
/// <summary>
/// .pubxml 是"发布配置"，只属于声明它的项目；平台由外层决定，不得写死 RID。
/// </summary>
[Fact]
public void PublishProfiles_ShouldNotHardcodeRuntimeIdentifier()
{
    foreach (var profile in new[]
    {
        "samples/Seeing.Agent.WebUI/Properties/PublishProfiles/WebUI-linux-x64.pubxml",
        "samples/Seeing.Agent.Desktop/Properties/PublishProfiles/FolderProfile.pubxml",
        "samples/Seeing.Agent.Cli/Properties/PublishProfiles/FolderProfile.pubxml",
    })
    {
        var xml = ReadRepoFile(profile);
        Regex.IsMatch(xml, @"<RuntimeIdentifier>", RegexOptions.CultureInvariant)
            .Should().BeFalse($"{profile} 不得写死 RuntimeIdentifier —— 平台由外层 -r 决定");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~PublishProfiles_ShouldNotHardcodeRuntimeIdentifier"`
Expected: FAIL（WebUI 与 Desktop 的 pubxml 均含 `<RuntimeIdentifier>`）

- [ ] **Step 3: 清理 pubxml**

- `FolderProfile1.pubxml` → `WebUI-linux-x64.pubxml`：删除 `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>`，
  并在文件头加注释说明"平台由 -r 或 VS 发布对话框决定；此名称仅标记曾用于 linux-x64 验证"。
- `Desktop/FolderProfile.pubxml`：删除 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`，
  改为注释说明"Desktop 仅支持 Windows，由 Task 4 的守卫强制"。
- `Cli/FolderProfile.pubxml`：已无 RID，仅统一注释。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~DesktopPublishProfileIsolationTests"`
Expected: 全部 PASS

- [ ] **Step 5: 实测 VS 路径仍可用（RID 由命令行提供）**

```powershell
$o='C:\Users\quand\AppData\Local\Temp\opencode\t6'; Remove-Item $o -Recurse -Force -EA 0
dotnet msbuild samples\Seeing.Agent.WebUI\Seeing.Agent.WebUI.csproj -restore -t:Build `
  -p:Configuration=Release -p:RuntimeIdentifier=linux-x64 `
  -p:PublishProfile=WebUI-linux-x64 -p:DeployOnBuild=true -p:PublishUrl="$o" -v:m
Get-ChildItem $o -Filter '*ChannelHost*'; Get-ChildItem $o -Filter 'libe_sqlite3*'
```
Expected: ChannelHost 四件套 + `libe_sqlite3.so` 均在

- [ ] **Step 6: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Properties/PublishProfiles samples/Seeing.Agent.Desktop/Properties/PublishProfiles samples/Seeing.Agent.Cli/Properties/PublishProfiles tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs
git commit -m "chore(publish): pubxml 移除写死的 RID，平台改由外层决定"
```

---

### Task 7: 全矩阵回归验证

**Files:**
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Hosting/DesktopPublishProfileIsolationTests.cs`

**Interfaces:**
- Consumes: Task 1–6 全部

- [ ] **Step 1: 跑完整守门测试套件**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Release/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~DesktopPublishProfileIsolationTests"`
Expected: 全部 PASS

- [ ] **Step 2: 跑 WebUI 全量测试**

Run: `dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Release/net10.0/Seeing.Agent.WebUI.Tests.dll`
Expected: 全部 PASS（确认未波及其它测试）

- [ ] **Step 3: 跑 Cli 相关测试**

Run: `dotnet build tests/apps -c Release` 后对所有 `Seeing.Agent.Cli.Tests` 程序集执行 vstest
Expected: 全部 PASS

- [ ] **Step 4: 执行四路径 × 两 RID 矩阵并记录**

按 spec §6 表格逐条执行，确认：
- ChannelHost 四件套齐全且无 apphost
- 原生库与 RID 匹配（linux → `libe_sqlite3.so`；win → `e_sqlite3.dll`）
- Cli 的 linux 包内无 `.exe`

- [ ] **Step 5: 提交（若有文档更新）**

```bash
git add docs/superpowers/specs/2026-11-07-publish-path-unification-design.md
git commit -m "docs(publish): 补充全矩阵回归实测结果"
```
