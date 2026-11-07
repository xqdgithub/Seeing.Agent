# 工作区首页与启动决策设计

**日期:** 2026-11-07
**状态:** 已按架构审查修订，待审阅
**关联:** `docs/architecture/03-configuration-lifecycle.md`、`docs/architecture/30-desktop-host-release-notes.md`

## 1. 背景与目标

今天打开 WebUI / Desktop 会直接进入控制台 `/`。工作区由 `WorkspaceProvider.InitializeAsync` 决定，顺序是环境变量 `SEEING_WORKSPACE_ROOT`、项目 `Workspace.CustomPath`、`UseGlobal` 时的用户级 `GlobalWorkspaceRoot`、最后是进程启动目录。切换入口在系统设置里，是一个手填路径的对话框，最近列表私存在 `~/.seeing/workspace_history.json`。

WebUI 与 Gateway 在同一进程、同一个 `IWorkspaceProvider` 上。页面上的「启动 Gateway」只是打开这个进程里的服务。渠道子进程用自己的 `WorkspaceProvider`，不会自动跟着 WebUI 的根走。

`WorkspaceProvider` 构造时就把当前目录记成工作区根。`ProjectSeeingDirectory` 是这个根下的 `.seeing`。会话库、会话组、Quartz 连接串、ChannelHost 状态目录、复盘目录都在第一次解析时把该路径拷走。只让 `GetProjectRoot()` 抛异常，挡不住这些读点。

**目标：**

1. 有界面的宿主在能够确定工作区时直接进入现有系统页；确定不了才显示工作区首页。
2. 首页用卡片列出最近工作区，并允许选一个文件夹作为新工作区。
3. 进入系统后，侧栏可以切换工作区。切换只影响本进程，完成后整页进入新工作区的控制台。
4. 可以同时开两个 WebUI，各自用不同目录。
5. CLI / TUI 每次使用自己的启动目录。TUI 已有的位置参数仍是这次要进入的目录。

**硬约束：**

1. 项目路径在决议完成之前不可读。未选定期间不结算、不 Activate、不 `LoadAll`。
2. 首次选定和侧栏切换走同一条 `Apply`：设置根、重读项目配置、只 await 一轮全量重载。不另写一套装载。
3. Gateway 不拆进程。它跟随所在 WebUI 进程的工作区。渠道子进程和调度库在切换时按新根重新拉起。
4. 用户级 `seeing.json` 不再提供一份「所有进程共用」的钉住目录。
5. 启动不再读取 `Workspace.CustomPath`、`UseGlobal`、`GlobalWorkspaceRoot`。这些键保留，旧文件仍能反序列化，但不再参与启动，也不再被 `SetWorkspaceOptionsAsync` 重新套用。

## 2. 已锁定的决策

| 决策 | 选择 |
|------|------|
| 冷启动能进就进 | 当前目录是打开过的工作区则直接进入；否则进入上次在界面里选定的；都没有才显示首页 |
| 定工作区的时机 | 构建会读取项目路径的服务之前。决策由宿主传入，不在 `WorkspaceProvider` 构造后再猜 |
| 两个实例如何分开 | 进程参数 `--workspace <目录>` 优先；父进程传入的 `SEEING_WORKSPACE_ROOT` 次之；都没有才走界面规则 |
| `--workspace` 与双击默认 | 启动时只记入最近列表，不改写「双击时进入的工作区」。之后在这个进程里用界面切换，会改写 |
| CLI / TUI | 工作区就是本次启动目录或 TUI 的位置参数。shell 里残留的同名环境变量不盖过它。子进程的当前目录和 `SEEING_WORKSPACE_ROOT` 都写成这次决议的目录 |
| 切换时的在途执行 | 没有在途就直接切。有在途先确认。确认后先拒绝未决审批，再取消，并等到 `HasInFlight()` 为 false，然后才改根 |
| 切换后落点 | 现有控制台 `/` |
| 设置页里的旧切换 | 去掉。启动目录 / 全局默认 / 自定义路径不再提供编辑。当前工作区改为只读。文件硬边界保留 |
| 选择文件夹 | 浏览器和桌面共用一个目录选择组件。目录列表由 WebUI 进程内的目录服务提供 |

## 3. 启动决策

纯函数 `WorkspaceLaunchPolicy.Resolve` 放在 Core。返回值里带上历史写法（是否更新 `recent`、是否更新 `lastInteractive`），宿主按返回值写历史，不再各自复述第 4 节。

路径是否相同，用 `PathBoundary.PathComparison`：Windows 忽略大小写，Linux 区分大小写。比较前去掉尾部分隔符并取绝对路径。

### 3.1 输入

```text
mode                  Invocation | Interactive | Service
startupDirectory      用户启动进程时的当前目录，尚未被宿主改写
executableDirectory   有界面宿主的 exe 目录；其他宿主可空
explicitPath          --workspace，或 TUI 的位置参数；没有则为空
inheritedEnvironment  进程已有的 SEEING_WORKSPACE_ROOT；没有则为空
history               见第 4 节
```

Invocation 不读取 `inheritedEnvironment`。那是父进程交给子进程的通道，父进程必须同时把子进程的当前目录设成已决议的工作区。

### 3.2 各宿主怎么填输入

| 宿主 | 模式 | 启动目录 | 显式路径 |
|------|------|----------|----------|
| WebUI | Interactive | 进程当前目录 | `--workspace` |
| Desktop | Interactive | `DesktopWorkspace.Apply()` **之前**捕获的原始目录，作为参数传给 `WebUiHost.BuildAsync` | `--workspace` |
| CLI | Invocation | 进程当前目录 | 无。`CliWorkspace`、status、stop、config 都调用这一个函数 |
| TUI | Invocation | 进程当前目录 | 位置参数。没有位置参数时工作区就是启动目录 |
| Gateway.Server | Service | 进程当前目录 | `--workspace` |

ChannelHost 子进程继续用 `new WorkspaceProvider(从配置路径推断的根)`，不跑 `InitializeAsync`，也不套这张表。WebUI 切换时停掉这些子进程，再用新根拉起。

桌面在捕获原始目录之后，只有决策为未选定时才把进程当前目录挪到可写位置，避免停在 `Program Files`。这个位置不写入 `StartupDirectory`，不当作工作区，不进入历史。`WebUiHost` 内部不再调用 `GetCurrentDirectory()` 来决议工作区。

### 3.3 Invocation（CLI、TUI）

1. 有 `explicitPath` 就用它，否则用 `startupDirectory`。
2. 目录不存在：非零退出，说明路径不存在。
3. 返回「更新 `recent`，不更新 `lastInteractive`」。

### 3.4 Interactive（WebUI、Desktop）

按顺序命中一条即停止：

1. **`--workspace` 非空。** 目录必须存在，否则进程启动失败并写明路径，不回退。返回「更新 `recent`，不更新 `lastInteractive`」。
2. **环境变量非空。** 目录必须存在，否则启动失败，不回退。历史写法与上一步相同。写在用户环境里的同名变量也会命中；要让两个实例进不同目录，用各自的 `--workspace`。
3. **启动目录命中最近列表。** 启动目录存在、与 exe 目录不同、且在 `recent` 中：使用它。不更新 `lastInteractive`。
4. **`lastInteractive` 仍存在：** 使用它。不把这次自动进入再写回历史。
5. **否则：未选定。**

exe 目录不参与第 3 步。便携布局把 exe 放在工作区根上时，双击进入 `lastInteractive` 或首页。

### 3.5 Service（单独的 Gateway.Server）

没有首页。顺序为 `--workspace`、已传入的环境变量、启动目录。被选中的目录不存在则启动失败。命中后只更新 `recent`。不再读取 `CustomPath` / `UseGlobal`。启动日志写明这次用的是参数、环境变量还是启动目录。WebUI 里联动启动的 Gateway 不走这里。

### 3.6 未选定与已决议怎么进初始化

决策在 `Host.Build` 之前算完，并传进 `WorkspaceProvider`。构造函数在未选定时不得把当前目录公布成工作区根。

- **已决议：** 构造完成时根就是该路径。`InitializeSeeingAsync` 按这个根加载项目配置、结算、Activate、`LoadAll`，与今天成功启动的后半段相同。
- **未选定：** `ProjectSeeingDirectory` 和 `GetProjectRoot()` 都抛 `InvalidOperationException`。`InitializeSeeingAsync` 只加载用户级配置，不结算、不 Activate、不 `LoadAll`。`Host.Start` 仍会挂上重载处理器，这是预期行为。

下列对象今天在构造或首次解析时拷走项目路径。未选定期间不得解析它们；实现要么推迟到 `Apply` 之后，要么改为每次向 `IWorkspaceProvider` 要路径，并在工作区变更时重定位：

- `FileSessionStore`、`FileSessionGroupStore`
- `ReflectionStorageRoot`
- `ChannelHostConfigStore`、`WeComSessionTracker`
- Quartz 的 `Data Source`（注册时写死的连接串）
- `AgentManager` 对项目级 `AGENT.md` 的扫描
- `SessionArchivalScanService` 的首次列举

`GatewayHostedService` 和 `ChannelHostHostedService` 的 `StartAsync` 只跑一次。未选定时它们直接返回。首次 `Apply` 成功后必须按原有开关再启动它们，不能指望宿主把 `StartAsync` 再调一遍。调度器同理：未选定期间不触发；`Apply` 之后按新库启动。

`SetWorkspaceOptionsAsync` 只保存仍有效的项目选项（文件硬边界）。它不再调用 `InitializeAsync`，避免把一次运行中的切换打回启动目录。

## 4. 历史

唯一文件：`~/.seeing/workspace_history.json`。所有宿主共用。实现放在 Core，经 Abstractions 上的 `IWorkspaceHistoryStore` 注入。Core 增加对原语项目 `Seeing.Files` 的向下引用。

```json
{
  "recent": ["E:\\work\\a", "D:\\work\\b"],
  "lastInteractive": "E:\\work\\a"
}
```

| 字段 | 含义 |
|------|------|
| `recent` | 打开过的目录，最前是最新。上限 30。同一路径只留一条。不存在的目录不参与自动进入，下次写入时删掉 |
| `lastInteractive` | 上次在界面里选定的目录。双击且当前目录不能命中 `recent` 时进入这里 |

谁更新哪个字段，只由 `Resolve` 的返回值和界面 `Apply` 决定：

- 启动决议：按第 3 节的返回值。
- 界面选定或侧栏切换：同时更新 `recent` 和 `lastInteractive`。由 `--workspace` 或环境变量启动的进程，之后在界面里的切换也更新 `lastInteractive`。

旧的字符串数组按「最前为最新」读成 `recent`，第一条仍存在的路径同时成为 `lastInteractive`。下次保存写成对象。文件损坏时当作空历史。

读-改-写整段持有跨进程命名互斥量，进程内再持有 `AtomicFileWrite.GetLock`，然后替换文件。`recent` 按路径取并集再套上限，避免一个实例的整文件覆盖抹掉另一个实例刚加入的目录。`lastInteractive` 以后写入的为准。

## 5. 工作区首页

路由 `/workspaces`。未选定时它是唯一可达的页面，使用不含侧栏的布局。

页面内容：

- 标题「选择工作区」。
- 最近目录卡片。展示文件夹名和完整路径。`lastInteractive` 那张标「默认」。
- 「选择文件夹」，打开第 7 节的 `DirectoryPicker`。
- 没有可用卡片时只保留选择文件夹和一句说明：还没有工作区，选一个文件夹开始。
- 失败时留在本页并显示原因。

未选定时选定一个目录：走第 6 节的 `Apply`。历史由 `Apply` 成功之后按第 4 节写入。这时没有在途执行，不弹确认。`Apply` 里的那一轮重载就是第一次结算和 Activate。旁边不再调用第二套装载。

已经有工作区时再打开 `/workspaces`，页面相同。选定另一个目录也走第 6 节，含在途确认。侧栏不必先进入这个页面。

## 6. Apply

侧栏底部显示当前工作区的文件夹名，悬停可见完整路径。列表含最近目录和「选择文件夹」。点当前项无动作。

`IWorkspaceSwitch.ApplyAsync` 是首次选定和再次切换的唯一下口：

1. 目标与当前根相同：结束。未选定状态下没有当前根，不比较。
2. `HasAnyActiveExecution()` 为真：确认框说明条数。能取到会话标题就列出，取不到用会话 id。用户取消则结束。
3. 拒绝本进程全部未决审批：`GetAllPending()` 逐条 `TryResolve`，`PermissionResolvedBy.Cancellation`。
4. `CancelAllInFlightAsync()`。它只发出取消，不等循环结束。接着按活跃会话 `WaitForIdleAsync`，直到 `HasInFlight()` 为 false。
5. 暂停新的入站投递和调度触发，避免去抖窗口里再开一轮。
6. `SetWorkspaceRoot`。全量重载的旧根、新根用这次事件的真实路径，不用 `StartupDirectory` 冒充旧根。
7. `UnifiedConfigManager.LoadAsync()`，按新的 `ProjectSeeingDirectory` 重读项目配置。编排器自己不读盘。
8. await 一轮全量重载。这一轮返回每个处理器的成败。只等到静默不算成功：处理器失败今天会被编排器吃掉。
9. 确认会话存储的基目录已经是新工作区下的 `sessions`。调度器改到新工作区的 `quartz.db`（停掉后按新路径重建）。停掉旧 ChannelHost 子进程，按新根再拉起。首次 `Apply` 时按原开关启动 Gateway、ChannelHost 和调度器。
10. 恢复投递和调度。按第 4 节写历史。界面上 `NavigateTo("/", forceLoad: true)`。

切换进行中不可再次进入。任一步失败：不整页跳转。根已经改过则设回旧根，再 `LoadAsync` 并重载一轮。回滚本身失败时停在错误状态并向用户显示，不假装仍在旧工作区。

## 7. 选择文件夹

浏览器和桌面共用 `DirectoryPicker`。不手填路径，不用系统原生对话框。

### 7.1 服务

`IDirectoryBrowser` 放在 WebUI 宿主内，由 Blazor 组件在进程内调用。不新增 HTTP 路由。不读取 `IWorkspaceProvider`，不经过 `IWorkspacePathGate`、`PathBoundary` 或 `Seeing.Agent.WebUI.Files`。那些能力只描述当前工作区。

信任模型和今天的 WebUI 相同：能打开页面的一方就能调用它。WebUI 没有单独的登录。桌面和 CLI 把监听放在回环地址上；一旦用 `--urls` 或关闭 HTTPS 跳转把页面暴露到局域网，选择器和页面的其余部分一样暴露。选择器不单独增加认证。非回环部署若以后给页面加认证，选择器走同一道认证。

每条路径都是不可信输入。服务规范化后再访问。列目录和创建只接受「当前对话框已经列出的那个父目录」；调用方不能拿一条从未列出的绝对路径来创建。确认选择时再次检查目标存在且是目录。

| 操作 | 行为 |
|------|------|
| 列根 | Windows 返回就绪的驱动器。其他系统返回 `/`；用户主目录与 `/` 不同时再加一项 |
| 列子目录 | 只返回目录，按名称排序，比较规则用 `PathBoundary.PathComparison`。不返回文件，不读文件内容 |
| 新建子目录 | 父目录必须是本次对话框列出的当前目录。名称不得包含路径分隔符或 `..`。操作系统拒绝时，把错误显示在对话框里 |
| 确认选择 | 目标必须是本次列出的当前目录或其中一项，且仍是目录 |

目标不存在或不可读时返回错误说明，不抛到页面。不可进入的子目录从列表中略过。不提供删除、重命名、移动。

### 7.2 组件

`DirectoryPicker` 是对话框，首页和侧栏都引用这一处。已有工作区时从该目录列起。尚未选定时从用户主目录列起，主目录不可用时列根。

点击子目录进入，可回上级或点根。选中的是当前所在目录。「新建文件夹」成功后进入该文件夹。取消则不 `Apply`。

任何存在的目录都可以成为工作区。不要求已有 `.seeing`。`.seeing` 由 `Apply` 之后的正常装载创建。

## 8. 设置页

系统设置「工作区设置」中删除：

- 「切换」按钮
- 「工作区模式」
- 「全局默认工作区」
- 「自定义路径」
- 三个 `WorkspaceSelectorModal`

保留当前工作区路径（只读，说明改到侧栏切换）和「工作区文件硬边界」。`WorkspaceSelectorModal` 删除。最近列表只由 `IWorkspaceHistoryStore` 读写。

## 9. 错误处理

| 情况 | 行为 |
|------|------|
| `--workspace`、TUI 位置参数或继承的环境变量指向不存在的目录 | 进程启动失败，不回退 |
| CLI / TUI 启动目录不存在 | 非零退出 |
| 选出的目录在确认时已不存在 | 留在原地，显示「目录不存在」 |
| 新建文件夹被操作系统拒绝 | 对话框显示该错误 |
| 历史文件损坏 | 当作空历史 |
| 历史写入失败 | 记录日志。`Apply` 仍继续。下一次冷启动可能不记得这次选择 |
| 未选定期间读取 `ProjectSeeingDirectory` 或 `GetProjectRoot()` | 抛 `InvalidOperationException` |
| 重载处理器报告失败 | 不整页跳转，按第 6 节回滚 |
| 切换过程中再次点击 | 忽略，直到本次结束 |

## 10. 测试

策略与历史放在 `tests/spine/Seeing.Agent.Tests`。不使用固定等待。

- Invocation 使用启动目录，忽略残留环境变量。TUI 位置参数优先于启动目录。目录不存在则失败。
- Interactive：`--workspace` 优先于环境变量，环境变量优先于历史。两者指向缺失目录时失败。
- 启动目录等于 exe 目录时不命中 `recent`。在 `recent` 中则使用它，且返回值不要求更新 `lastInteractive`。
- 否则使用仍存在的 `lastInteractive`。都没有则未选定。
- 由显式路径启动之后的界面 `Apply` 仍更新 `lastInteractive`。
- 旧字符串数组读成 `recent`，第一条现存路径成为 `lastInteractive`。
- 两个写入者合并 `recent`，不丢其中一方刚加入的路径。`lastInteractive` 以后写的为准。不存在的路径在写入时删除。超过 30 条丢掉最旧的。
- 未选定的 `WorkspaceProvider` 读取项目目录时抛异常，且构造时的当前目录不会被读成项目根。
- `SetWorkspaceOptionsAsync` 不再调用启动决议。

WebUI 测试放在 `tests/apps/Seeing.Agent.WebUI.Tests`：

- 无在途时 `Apply` 的顺序是设置根、`LoadAsync`、一轮重载，然后导航到 `/` 并整页重载。
- 有在途且用户取消：不取消执行，不改根。
- 有在途且用户确认：先拒绝未决审批，再取消，并且在 `HasInFlight()` 变为 false 之后才改根。
- 重载结果含失败时不导航，并要求把根设回旧值。
- 未选定时路由只渲染工作区首页。首次选定走同一个 `Apply`，不存在第二条装载入口。

目录服务用临时目录测试：

- 列表里只有子目录。
- 目标不存在时返回错误，不抛异常。
- 新建只接受已列出的父目录。名称含分隔符或 `..` 时拒绝。
- 服务在工作区未选定时仍能列出临时目录。

`Seeing.Agent.Desktop.Tests` 锁住：原始启动目录在 `Apply()` 改当前目录之前捕获，并传给 WebUI 宿主。改写后的当前目录不参与决议。

既有 `WorkspaceSwitchConfigReloadTests` 里「环境变量 + `UseGlobal` 经 `InitializeSeeingAsync` 决议」的断言改为新矩阵，不再把 `UseGlobal` 当作启动来源。

## 11. 不在本次范围

- 把 Gateway 拆成独立进程，让它停在另一个目录而 WebUI 继续切换。
- 删除配置模型里的 `GlobalWorkspaceRoot`、`UseGlobal`、`CustomPath` 字段。
- 把旧的全局默认或自定义路径迁移进 `lastInteractive`。升级后若历史为空，有界面宿主显示首页。单独的 Gateway.Server 改为参数、环境变量或启动目录，并在日志里写出来源。
- 为两个实例设计端口规则。Desktop 已会从首选端口起寻找空闲端口。
- 按会话使用不同工作区。
- 手填路径、系统原生文件夹对话框，以及公开的目录枚举 HTTP 接口。
- 在目录选择里删除、重命名或移动文件夹。
- 为 WebUI 新做一套登录。选择器的暴露面与现有页面相同。
