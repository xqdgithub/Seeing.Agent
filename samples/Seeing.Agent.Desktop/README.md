# Seeing 桌面版

把 WebUI 打包成双击即启动的原生窗口应用。设计与约束详见
[桌面宿主 Release Notes](../../docs/architecture/30-desktop-host-release-notes.md)。

```bash
# 开发期直接运行（窗口 + 同进程 Kestrel）
dotnet run --project samples/Seeing.Agent.Desktop
```

## 打包

**框架依赖（推荐，也是唯一受支持的方式）**

```bash
# Windows x64
dotnet publish samples/Seeing.Agent.Desktop -c Release -r win-x64 -o artifacts/seeing-win-x64

# macOS Apple Silicon
dotnet publish samples/Seeing.Agent.Desktop -c Release -r osx-arm64 -o artifacts/seeing-osx-arm64

# Linux x64
dotnet publish samples/Seeing.Agent.Desktop -c Release -r linux-x64 -o artifacts/seeing-linux-x64
```

产物约 56 MB，入口是 `Seeing.exe`。目标机器需预装 **.NET 10 运行时**。

### 为什么不能自包含

WebUI 的 `OutputType` 是 `Exe`（CLI 的 `seeing web` 需要独立启动
`Seeing.Agent.WebUI.dll`），而 SDK 规则 **NETSDK1150** 禁止
「自包含的可执行文件引用非自包含的可执行文件」。

命令行加 `-p:SelfContained=true` 之所以看着能用，是因为该属性作为**全局属性**
被广播到所有项目；Visual Studio 的发布配置不会广播，于是一发布就失败：

> 引用的项目"..\Seeing.Agent.WebUI\Seeing.Agent.WebUI.csproj"是非自包含的可执行文件。
> 非自包含可执行文件不能由自包含可执行文件引用。 (NETSDK1150)

为让两条发布路径行为一致，`Seeing.Agent.Desktop.csproj` 已显式声明
`<SelfContained>false</SelfContained>`，`FolderProfile.pubxml` 也不再设置该属性。

若确实需要自包含，仍可用命令行显式指定（全局属性会覆盖项目级声明）：

```bash
dotnet publish samples/Seeing.Agent.Desktop -c Release -r win-x64 -p:SelfContained=true -o artifacts/seeing-win-x64-sc
```

但**不要**把该开关写进 `.pubxml`，也不要在 VS 发布界面选「独立」部署模式。

**为什么不用 `PublishSingleFile=true`**：PhotinoX 依赖 `PhotinoX.Native` 与
`WebView2Loader` 等原生库，单文件模式需 `IncludeNativeLibrariesForSelfExtract`
解包到临时目录，首次启动明显变慢，且部分 Linux 发行版 `/tmp` 挂载为 `noexec`
时会直接失败。

**为什么不用 NativeAOT**：Blazor Server 通过反射发现 Razor 组件、SignalR 负责序列化，
AOT 下需要大量裁剪配置，当前收益不足以抵消风险。
