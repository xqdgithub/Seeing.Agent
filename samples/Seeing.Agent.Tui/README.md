# Seeing.Agent.Tui（`seeing-tui`）

内联流式终端聊天入口（TUI），核心能力对齐 WebUI 的「会话聊天」：新建/继续会话、流式文本与推理、工具卡、Markdown、权限审批与问答内联作答、子代理进度、Todo 与 Token 预算状态、斜杠命令。

- 程序集名：`seeing-tui`
- 与 WebUI 共享 **同一工作区** 下的会话数据：`<workspace>/.seeing/sessions/`
- 只读现有配置，**不提供任何配置编辑 UI**（无设置页/模型管理/Gateway 管理）

> 设计规格见 [`docs/superpowers/specs/2026-09-21-tui-entry-design.md`](../../docs/superpowers/specs/2026-09-21-tui-entry-design.md)。

## 运行

```bash
dotnet run --project samples/Seeing.Agent.Tui
```

需要**交互式 TTY**。若 `stdin`/`stdout` 被重定向（非 TTY），程序打印错误并**退出码 2**，不启动。

> 启动时由 `TuiConsoleEncoding.EnsureUtf8()` 自动将控制台输入/输出编码置为 UTF-8（代码页 65001）：系统默认 GBK(936) 会让中文输入被按 GBK 送字节、按 UTF-8 解码而乱码，且非 936 可编码的字形会回退成字面 `?`。退出时 `TuiConsoleEncoding.Restore()` 还原原编码，避免残留 65001；无控制台/重定向时该设置静默跳过。

### 启动参数

| 参数 | 说明 |
| --- | --- |
| `[workspace]` | 工作区路径（位置参数，默认当前目录）；经 `SEEING_WORKSPACE_ROOT` 注入 |
| `--continue`、`-c` | 继续最近更新的会话 |
| `--resume <id>` | 恢复指定会话 Id；会话不存在时**退出码 3** |
| `--agent <name>` | 覆盖默认 Agent |
| `--model <id>` | 覆盖默认模型 |
| `--log-level <level>` | 日志级别：`Trace`/`Debug`/`Information`/`Warning`/`Error`/`Critical`/`None`（同义 `warn`/`fatal`/`off`；默认 `Information`） |
| `--boot <name>` | 进程启动能力天花板覆盖（`Boot`）；由 `BootOverrideSource` 直接消费原始 `args`（亦可用环境变量 `SEEING_BOOT`），不映射到 TUI 选项 |

示例：

```bash
dotnet run --project samples/Seeing.Agent.Tui -- . --continue
dotnet run --project samples/Seeing.Agent.Tui -- --resume <sessionId> --agent build --log-level Debug
```

## 配置

- 读取用户级 `~/.seeing/seeing.json` 与项目级 `.seeing/seeing.json` 的 `DefaultAgent` / `DefaultModel`（由 Core 配置解析）；`--agent` / `--model` 可覆盖。
- 会话级 Agent / 模型 / 思考档 / 场景 / 审批模式由斜杠命令切换并写回会话。
- TUI 不解析、不修改配置文件本身。

## 平台支持

| 平台 | 状态 | 说明 |
| --- | --- | --- |
| Windows | 支持 | 启用虚拟终端输入模式；停止时注入控制台哨兵事件解除阻塞读。 |
| Linux | 支持 | termios raw + `poll` 可取消读。 |
| macOS | **尽力支持（未经实机验证）** | 与 Linux 同样走 termios raw + `poll`，但 termios 结构与标志位按 Darwin 定义、未在实机验证；初始化失败时降级为托管流读取（按键需回显/回车，停止时读取线程随进程退出）。 |
| 其他 Unix | 受限 | 无 termios 适配，显式降级为托管流读取。 |

- Unix 不设置 `Console.TreatControlCAsInput`（该 API 在 Unix 抛异常）：raw 模式下 `Ctrl+C` 作为字节 `0x03` 解析；`SIGINT` / `SIGTERM` 由 Host 生命周期统一处理，触发引擎取消（`ApplicationStopping`）后主循环退出、`host.StopAsync()` 正常返回，进程不再挂住。


## 起始页

空闲且尚未产生对话内容时，活动区顶部显示 `SEEING` ASCII Logo 与常用操作提示（`Enter` / `Ctrl+J` / `Tab` / `/model` `/agent` / `@path` / `Esc Esc` / `/exit`）。

- 首次提交（或恢复已有会话）后自动消失，**不写入滚动历史**，不占用后续回合的空间。
- 终端宽度 < 42 列时自动降级为单行 `S E E I N G` + 精简提示集，避免折行破版。
- 全部文案限 ASCII 与 CP936 可编码字符（同 `TuiGlyphs` 约束），并有 GBK 往返测试兜底。

## 键盘操作

| 按键 | 行为 |
| --- | --- |
| `Enter` | 提交输入 |
| `Ctrl+J` | 插入换行（多行输入） |
| `Shift+Enter` | 插入换行；仅终端发送可区分序列（Kitty/CSI-u）时生效，否则回退 `Ctrl+J` |
| `↑` / `↓` | 召回输入历史（仅内存，最多 100 条） |
| `←` / `→` | 移动光标 |
| `Backspace` / `Delete` | 删除 |
| `Tab` | 行首 `/命令` 补全：唯一候选直接补全并在其后加空格；多候选弹出选择器；无候选忽略 |
| 输入 `/` | 行首输入斜杠命令时，**实时在输入行上方显示候选表**（命令名 + 说明，上限 8 行）；出现空白后不再显示 |
| `Ctrl+C` | 输入非空时清空；执行中**立即**取消当前执行（明确意图，无需二次确认）；**空闲且输入为空时二次确认退出** |
| `Esc` | 输入非空时清空；执行中**需连按两次**才取消执行（首次在状态栏提示「再按一次 Esc 取消执行」，**3 秒**内再按生效，超时需重新两次）；**提示中取消并返回**（`/model`、`/agent`、`/sessions`、`/thinking`、`/scenario`、补全候选、权限=不决策、**问答=取消作答并回到聊天窗口**） |
| `Ctrl+D` | 退出 |

## 斜杠命令

本地命令由 TUI 拦截执行；其中 `/attach`、`/detach`、`/rename`（无参）、`/delete` 由**引擎侧**（`TuiChatEngine`）拦截并交互，其余本地命令由 `TuiCommandRouter` 分流。未命中的 `/xxx`（含未知命令）作为普通输入提交给服务端处理。

**本地拦截**

| 命令 | 说明 |
| --- | --- |
| `/help`、`/h`、`/?` | 本地帮助（合并 `ICommandRegistry` 元数据） |
| `/new [title]` | 新建会话并切换 |
| `/sessions [id]` | 无参交互选择会话；带参列出会话（表格） |
| `/resume [id]` | 无参交互选择会话；带参切换到指定会话 |
| `/rename [title]` | 无参交互输入新标题；带参直接重命名 |
| `/delete` | 二次确认后删除活跃会话 |
| `/fork` | 分支活跃会话并切换 |
| `/open <id>` | 打开指定会话/子会话（子代理场景，同 `/resume` 语义） |
| `/agent [name]` | 无参交互选择 Agent；带参直接设置 |
| `/model [id]` | 无参交互选择模型；带参直接设置 |
| `/thinking [level]` | 无参交互选择思考档；带参直接设置（`clear` 清除） |
| `/scenario [name]` | 无参交互选择场景；带参直接设置（`clear` 清除） |
| `/attach <path...>` | 读取本地文件暂存为待发附件（支持 `"含 空格"` 引号） |
| `/detach [n\|all]` | 移除第 n 个（从 1 起）或全部待发附件；无参移除最后一个 |
| `/auto-approve [follow\|on\|off]` | 会话级审批模式三态（无参数＝循环切换） |
| `/reasoning [on\|off]` | 推理显示开关（本地 UI 偏好） |
| `/cancel [all]` | 取消当前执行；`all` 为级联取消会话及子会话 |
| `/expand <callId>` | 展开工具完整输出 |
| `/todo` | 展开 Todo 面板 |
| `/exit`、`/quit`、`/q` | 退出 |

**服务端转发**（作为消息提交，由执行服务处理）

`/clear`、`/compact`、`/tools`、`/mcp`、Skill 命令，以及任何未命中本地清单的 `/xxx`（按普通输入下发，未知命令不会报错）。

## 权限审批与问答

- **权限**：内联弹出 `SelectionPrompt`，概览含「工具 / 归属 / 资源 / 匹配 / 风险 / 说明」。选项按请求的 `AllowedScopes` 过滤：
  - 本次允许（`Once`）
  - 始终允许（本会话）（`Session`）
  - 允许此目录（会话目录）（`SessionDirectory`）
  - 本次拒绝（`Deny` + `Once`）
  - 始终拒绝（`Deny` + `Session`）
- `Esc` 取消提示 = **不决策**（请求保持挂起，不会被误判为拒绝）。
- **问答**：按题型渲染——单选 `SelectionPrompt`、多选 `MultiSelectionPrompt`（非必填可空选）、文本 `TextPrompt`（支持默认值；单答案截断 2000 字符）；`AllowCustom` 时追加「其他（自定义）」自由输入项。
- 判定为「自动允许」的请求不进入队列、不弹提示；仅需用户决策（`Ask`）的请求才呈现。
- 提示期间按键由 `TuiPromptInputRelay` 分流到提示通道，不做 stdin 读取挂起。

## 子代理进度与 `/open`

- 主会话出现 `task` 工具调用时，`TuiTaskTracker` 解析其子会话（经会话组 + `origin_tool_call_id`）并订阅子会话事件流，把子工具调用聚合为进度步骤展示在工具卡上。
- 子代理进度**只写入 TUI 视图态**，不回写父会话 `SessionData`。
- 用 `/open <sessionId>` 进入子会话查看（子会话为会话组成员，审批可正常呈现）。

## 附件

- **内联附加**：输入以 `@path` 开头（可连续多个，支持 `@"含 空格 的路径"`）时，提交前读取为附件；`@path 正文` 会把 `正文` 作为文本、`path` 作为附件一起提交。
- **命令附加**：`/attach <path...>` 暂存附件（成功后输入行展示 `文件名 (大小)`）；`/detach [n|all]` 移除单个或全部。
- **纯附件提交**：文本为空但有待发附件时同样提交。
- 读取失败会回显错误并跳过该文件，不中断；提交成功后清空待发附件。
- 单个附件体积上限 **10 MB**，超限直接拒绝（提示实际大小与上限），避免整块读入内存并 Base64 常驻。
- MIME 由扩展名映射（含 `csv`），未知扩展名回退内容采样（PNG/JPEG/GIF/BMP/WebP/PDF），最终回退 `application/octet-stream`。

## 状态栏

状态栏占**两行**：

- **第 1 行**：Agent / 模型 / **审批模式** / 思考档（`think:`）/ 运行态 / 队列 / 待批 / 后台执行 / 提示 / Todo。
- **第 2 行（左右分栏）**：**左侧工作目录**（项目根，主目录缩写为 `~`），**右侧上下文用量**（如 `45.2k/200k (22%)`）。
  - 路径过长时**中间省略**（保留盘符与末段）；空间不足时路径让位给用量，用量右对齐保留。
  - 用量为 `已用/上限 (百分比)`；上限未知（模型未配置 context limit）时只显示已用量。首轮执行完成后开始更新。
- **审批模式**：对齐 WebUI 会话内分段控件「默认 / 自动 / 确认」，并显示**生效结论与来源**——
  - `审批 自动` / `审批 确认`：会话显式设置（`/auto-approve on|off`）；
  - `审批 自动(全局)` / `审批 确认(全局)`：会话为「默认」，跟随全局 `Permission.AutoApproveAll`（热重载实时反映）。
  - 自动批准时用黄色提示；切换用 `/auto-approve`（无参数循环：默认 → 自动 → 确认 → 默认），也可 `/auto-approve on|off|follow`。设置写回会话并立即生效。
- **换行保护**：两行各自恒为单行，且行尾保留 1 格（写满最后一格会触发终端自动换行、进而挤掉输入行）。宽度按**显示格**计算（CJK/全角记 2 格）；第 1 行超宽时按优先级丢弃低价值段（Todo → 思考档 → 后台执行 → 队列 → 模型），Agent / 执行态 / 审批模式始终保留。
- 「待批」= 在途权限请求 + 问答请求数；「后台执行」= 可呈现会话中除主会话外仍有未终态执行的会话数。

## 显示细节

- **推理**：默认折叠为尾部 **3 行**（首行 `* 思考（N 行，/reasoning 展开）`）；`/reasoning`（或 `/reasoning on|off`）切换完整显示。
- **活动区无独立「运行中」占位行**：执行态只在状态栏呈现（`运行中`/`空闲`），避免重复指示。
- **用户消息**：`> ` 前缀 + 蓝色（不加粗）渲染（与无前缀的助手 Markdown 正文区分），不加「你」标签行。
- **输入光标**：以 `▌` 按**真实插入位**渲染（文本分居光标两侧），`←`/`→` 可移动。
- **活动区高度**：`TailClipRenderable` 硬上限为终端高度 -1 行、裁尾保头（输入行 + 状态栏恒在末尾）；`Live` 帧 `AutoClear(true)` + `Overflow(Crop)` + `Cropping(Top)`。
- **斜杠命令候选**：行首 `/命令` token 内实时显示候选表（命令名 + 说明，上限 `TuiRenderOptions.MaxCompletionRows`=8），紧贴输入行上方；`Tab` 仍可直接补全或弹出选择器，选择器内 `Esc` 可取消返回。

## 日志

- 日志只写文件，不污染终端活动区（`ClearProviders` + 专用文件 Provider）。
- 实际路径：`~/.seeing/logs/seeing-tui-{yyyyMMdd}.log`（用户级目录；目录不存在时自动创建，Windows 下 `~` 为用户主目录）。

## 当前限制

- 非交互终端（stdin/stdout 被重定向）直接拒绝启动，退出码 2。
- `Shift+Enter` 在多数传统终端不可区分，请以 `Ctrl+J` 换行。
- 长回合流式期间按阈值固化稳定前缀到滚动历史，活动区只保留尾部（超出终端高度即裁尾）；工具输出超长时截断为预览，可用 `/expand <callId>` 展开。

## 测试

> 测试项目启用 MTP，但本环境无法发现测试；统一改用 VSTest 直跑已构建程序集（见根 `AGENTS.md`）。

```bash
dotnet build tests/apps/Seeing.Agent.Tui.Tests
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll
```

指定测试类：

```bash
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll --TestCaseFilter:"FullyQualifiedName~TuiCommandRouterTests"
```
