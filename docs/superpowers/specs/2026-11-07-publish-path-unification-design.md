# 发布路径统一设计（Seeing.Agent 宿主族）

**日期**：2026-11-07
**状态**：已完成实测校验，待评审
**范围**：`samples/Seeing.Agent.WebUI`、`samples/Seeing.Agent.Desktop`、`samples/Seeing.Agent.Cli`、`samples/Seeing.Gateway.ChannelHost`

> **本文档的所有结论均经实测验证**，验证命令与结果见 §7。
> 初稿中有三处推断被实测推翻，已在 §3.3 明确记录，避免后续实现按错误前提施工。

---

## 1. 意图

### 1.1 起点

现场症状：**VS 发布 WebUI，产物缺 `Seeing.Gateway.ChannelHost.dll`**（只剩两个 json），
运行期 `ChannelHostManager.ResolveChannelHost` 找不到可运行副本。

排查发现这是四个产物、四条发布路径长期各自演化的结果。本次目标不是再补一处特判，
而是**定一份统一发布契约**，让正确性来自结构。

### 1.2 四个产物的关系（实测核对）

| 产物 | OutputType | 与 WebUI 关系 | 进程模型 | RID 敏感度 |
|---|---|---|---|---|
| `Seeing.Agent.WebUI` | `Exe` (.NET Web) | — | 独立进程 | **强依赖**：原生库 `libe_sqlite3.so` / `e_sqlite3.dll`、apphost |
| `Seeing.Agent.Desktop` | `WinExe` (Photino) | **代码引用** `WebUiHost.BuildAsync` | **同进程内嵌** | **与 WebUI 同 RID**；受 `NETSDK1150` 约束不能自包含 |
| `Seeing.Agent.Cli` | `Exe` | **无代码引用**，装 WebUI 产物后跨进程拉起 | **跨进程** | 需**显式下传** RID |
| `Seeing.Gateway.ChannelHost` | `Exe`（`UseAppHost=false`） | 被 WebUI **运行时**拉起 | 跨进程 `dotnet *.dll` | **RID 无关** |

---

## 2. 目标 / 非目标

### 2.1 目标

1. **任一入口发布产物都完整**：VS 发布、`dotnet publish -r`、`dotnet publish -o`、`dotnet build`。
2. **一次发布一个 RID**，换 `-r` 即换平台；四个产物都支持跨平台。
3. **"补文件"逻辑只有一份**，新增产物只声明依赖。
4. 守门测试覆盖每条已知故障路径。

### 2.2 非目标

- 不做一次构建产出多 RID 胖包。
- 不改 `ChannelHostManager` 运行期探测（源码目录兜底保留给开发期）。
- 不引入外部构建工具。

### 2.3 反目标（做到也算失败）

- 靠多写 `Condition` 猜当前构建阶段 —— 这正是病根。
- VS 发布与 CLI 发布走两套逻辑。
- 继续在 `.pubxml` 里写死 RID。

---

## 3. 故障清单

### 3.1 已实测确认

| # | 故障 | 实测证据 |
|---|---|---|
| **F1** | VS / FileSystem 发布时 ChannelHost 的 `.dll` 落在收集目录之外 | `Z:\Seeing-Agent\publish`、`obj\...\PubTmp\Out`、修复前复现目录均**只 2 个 json** |
| **F2** | 目录属性算在独立 target 里，Build 阶段求值后冻结 | 诊断日志：`已跳过目标"PassChannelHostPublishDirs"。以前的生成已成功` |
| **F3** | **Cli 嵌套发布未传 RID**，跨平台包内含 Windows apphost | `linux-x64` 的 Cli 包里 `Seeing.Agent.WebUI.exe`、`seeing-tui.exe` 均为 Windows 二进制（162KB PE），Linux 上无法运行 |
| **F4** | WebUI 的 pubxml 写死 `RuntimeIdentifier=linux-x64` | `FolderProfile1.pubxml:16` |
| **F5** | "补 ChannelHost"逻辑存在多份且语义不同 | WebUI 两份 target、Desktop 一份、Cli 隐式一份 |

### 3.2 已实测排除（初稿曾误判为缺陷）

| 曾怀疑 | 实测结果 |
|---|---|
| Desktop 把 RID 漏给 WebUI 会产出错平台产物 | **正常**。`win-x64` 的 Desktop 包里原生库为 `e_sqlite3.dll`、apphost 正确，WebUI 跟随正确 |
| Desktop/Cli 的 ChannelHost 补齐逻辑应删除 | **不可删除**。`D:\AI\seeing-desktop`、`D:\AI\seeing-cli` 的四件套完整，正是靠它们各自的拷贝逻辑；删除会立刻缺文件 |

### 3.3 初稿中被推翻的推断（重要）

1. ~~"不指定 RID 时保持现有可移植行为"~~ —— **错**。
   实测 `dotnet publish -o`（无 RID）产出**缺 `libe_sqlite3.so`/`.dll`**，只有托管壳，
   该包实际不可用。**RID 是必需品，不是可选项。**
2. ~~"删除 Desktop/Cli 的 ChannelHost 补文件逻辑"~~ —— **错**，见 §3.2。
   `ReferenceOutputAssembly=false` 导致 `ProjectReference` 只带 json；
   Cli 的 `Exec publish` + staging 拷贝才拿到 `.dll`。两者机制不同，都不能简化掉。
3. ~~"Desktop 需要显式下传 RID 修正 bug"~~ —— **错**，实测已正确。
   真正的问题是命令行 `-p:RuntimeIdentifier` 未配合 `-restore` 才报 `NETSDK1047`，
   加 `-restore` 后正常。这是**用法**问题，不是产品缺陷。

---

## 4. 方案

### 4.1 三条候选

| 方案 | 做法 | 取舍 |
|---|---|---|
| **A 统一发布契约**（选定） | 抽共享 `.targets` 封装嵌套发布正确姿势；各宿主声明依赖 | 需改三个项目文件，但能同时覆盖 VS 与 CLI |
| B 叶子先发布 + 纯 Copy | 先发布到 `artifacts/<config>/<rid>/`，宿主只 Copy | 最干净，但 VS 发布按钮无法可靠带上编排 |
| C 各自修补 | 只补 F1/F3 | 最小，但重复逻辑保留，必然复发 |

选 A：F1–F5 多数源于"嵌套发布缺统一契约"。

### 4.2 设计要点

**(1) 单一契约文件 `build/Seeing.Publish.targets`**

被四个项目 Import，是唯一允许写嵌套发布的地方。导出：

- `_SeeingNestedPublish` —— 封装一次嵌套发布：
  - 显式传 `Configuration` / `TargetFramework` / `RuntimeIdentifier` / `PublishDir` / `SelfContained`
  - `RemoveProperties="PublishProfile;PublishUrl;WebPublishMethod"`（发布配置只属于声明它的项目）
  - **ChannelHost 额外 `RemoveProperties` 掉 `RuntimeIdentifier`**（RID 无关产物）
  - 目录参数**强制绝对路径**（相对路径以被调用项目目录为基准解析）
  - **不得透传 `BaseIntermediateOutputPath`**：实测透传会让被调用项目的
    `project.assets.json` 与 restore 视图错位。中间目录隔离改用
    `MSBuildProjectExtensionsPath` + 独立 restore（见 §5 待定项）
- `SeeingCopyPublishedFiles` —— 带 manifest 的目录复制（复用 Cli 现有 `.seeing-*-files.txt` 做法）

**(2) 目录属性一律在使用它的 target 体内计算（消灭 F2）**

取消 `PassChannelHostPublishDirs` 这类"供他人依赖"的属性 target。求值点 = 使用点。

**(3) 发布目录双写（消灭 F1）**

`dotnet publish` 与 VS/FileSystem 的落盘语义不同：

- `dotnet publish`：`$(PublishDir)` 即目标
- VS/FileSystem：`$(PublishDir)` 被改写为 `obj/.../PubTmp/Out/`（**收集源**），
  `FileSystemPublish` 再复制到 `$(PublishUrl)`

故嵌套产物必须**同时覆盖 `$(PublishDir)` 与 `$(PublishIntermediateOutputPath)`**（去重后各写一次）。
这是 F1 的直接解法，已实测有效。

**(4) RID 是必需品（修正初稿）**

实测证明无 RID 的发布缺原生库。因此：

- **发布必须带 RID**；未带时由契约给出明确诊断（而非静默产出坏包）
- `.pubxml` 删除 `RuntimeIdentifier`，平台由外层决定；VS 场景下由契约提供默认值
- 流向：

```
外层 -r <rid>
   ├─ WebUI        : 采用（不再取 pubxml）
   ├─ Desktop      : 采用，WebUI 跟随（已实测正确，保持现状）
   ├─ Cli          : 采用，**显式传给嵌套 WebUI / Tui 发布**（修 F3）
   └─ ChannelHost  : 无条件剥离
```

**(5) 每个产物保留自己的补齐逻辑，但改用契约实现**

F5 的正确解法不是"删除 Desktop/Cli 的补齐"，而是**让它们调用同一份契约**。
三处语义不同的实现收敛为一次 `_SeeingNestedPublish` 调用 + 一次 `SeeingCopyPublishedFiles`。

---

## 5. 落地清单

1. 新建 `build/Seeing.Publish.targets`（唯一嵌套发布出口）。
2. `Seeing.Agent.WebUI.csproj`：两个 target 改为调用契约；保留构建期同步到输出目录。
3. `Seeing.Agent.Desktop.csproj`：`CopyChannelHostToPublish` 改用契约；RID 保持现状（已正确）。
4. `Seeing.Agent.Cli.csproj`：两条 `Exec dotnet publish` **补 `-r $(RuntimeIdentifier)`**（修 F3）。
5. 清理三个 `.pubxml`：去掉 `RuntimeIdentifier`；`FolderProfile1` 改名。
6. 扩展守门测试。

**待定（需实现时定）**：中间目录隔离的具体手段。实测 `BaseIntermediateOutputPath` 透传有害，
需在实现阶段验证 `MSBuildProjectExtensionsPath` 或"独立 restore + 独立 out"方案，
并以其结果为准更新本文档。

---

## 6. 验证矩阵

断言产物含完整四件套 + 正确平台原生库：

| 路径 | 命令 | 实测状态 |
|---|---|---|
| VS 发布（RID 来自 pubxml） | `msbuild -t:Build -p:DeployOnBuild=true -p:PublishProfile=FolderProfile1` | ✅ 本修复后通过 |
| `dotnet publish -o`（无 RID） | `dotnet publish -c Release -o <dir>` | ✅ ChannelHost 正常；⚠ 缺原生库（RID 必需） |
| `dotnet publish -r -o` | `dotnet publish -c Release -r linux-x64 -o <dir>` | ✅ |
| `dotnet publish -r win-x64` | 同上 | ✅ 含 `e_sqlite3.dll` |
| Desktop VS 发布（带 restore） | `msbuild -restore -t:Build -p:DeployOnBuild=true` | ✅ |
| Cli `-r linux-x64` | `dotnet publish -c Release -r linux-x64` | ❌ **含 Windows apphost（F3）** |

> `-p:RuntimeIdentifier` 必须配 `-restore`，否则 `NETSDK1047`。这是用法约束，应写入文档与测试辅助。

---

## 7. 实测记录

验证环境：.NET SDK 10.0.403，Windows。

```
# F1 复现（修复前）
Z:\Seeing-Agent\publish        → 仅 deps.json + runtimeconfig.json
obj\Release\net10.0\linux-x64\PubTmp\Out → 同上
bin\Release\net10.0\linux-x64\publish    → 四件套完整（落错位置）

# F1 修复后（VS 路径，RID 来自 pubxml）
msbuild -t:Build -p:DeployOnBuild=true -p:PublishProfile=FolderProfile1 → 四件套完整 ✅

# F3 复现（Cli，linux-x64）
Seeing.Agent.WebUI.exe  162816 bytes  ← Windows PE，Linux 不可运行 ❌
seeing-tui.exe          162304 bytes  ← 同上 ❌

# RID 必需性
dotnet publish -o（无 RID） → 仅 SQLitePCLRaw.provider，缺 libe_sqlite3 ❌

# Desktop 已正确（无需修）
win-x64 → e_sqlite3.dll + 正确 apphost ✅
```

---

## 8. 风险

| 风险 | 缓解 |
|---|---|
| 契约文件改动波及 `Seeing.Agent.Cli.Tests` 等 | 先跑相关测试；保留既有 `Private=false` 规避（见 Cli csproj 注释） |
| 中间目录隔离方案未定 | §5 列为待定项，以实测结果为准，不预设结论 |
| 删除 pubxml 的 RID 后 VS 无默认平台 | 契约提供默认值 + 启动时校验 |
| VS 路径难在 CI 驱动 | 已验证 `msbuild -restore -t:Build -p:DeployOnBuild=true` 可复现 |
