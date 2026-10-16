# 工作区 `.seeing` 自动 Git 排除设计

**日期:** 2026-10-16
**状态:** 已批准（进入实现）
**关联:** `docs/architecture/02-modules.md`、`docs/architecture/03-configuration-lifecycle.md`

## 1. 背景与目标

Seeing.Agent 启动时会在工作区根目录创建 `.seeing/`（配置、会话、会话组、memory.db 等）。
当工作区本身是一个 git 仓库时，这些运行期产物会污染 `git status`，需要用户在**每个**仓库里手动
把 `.seeing/` 加进忽略清单。

**目标：** 启动时（及工作区切换时）自动把工作区的 `.seeing` 目录加入该仓库的忽略清单，使
`git status` 保持干净。

**硬约束：**

1. **不修改 `.gitignore`**（不改动被追踪文件，不产生仓库内 diff）。
2. 写入 git 的**本地、非追踪**排除文件 `.git/info/exclude`——仅本机本仓库生效，不影响协作者、
   不进入版本库、不需要提交。
3. 幂等：反复启动不重复写入；不破坏用户在 `info/exclude` 中的手写内容；与已有 `.gitignore`
   规则不冲突（已被忽略则不再写）。
4. 非 git 仓库 / 未安装 git / 无权限时静默降级（仅日志），不得阻断启动。

## 2. 方案选型（已决策）

| 决策点 | 选择 | 理由 |
|--------|------|------|
| 排除范围 | 仅本机本仓库（`.git/info/exclude`） | 不追踪、不影响协作者；符合「不改 .gitignore」 |
| 载体 | **新建能力模块** `Seeing.Agent.Workspace`，id `workspace.git-exclude` | 符合「Core 瘦脊柱 + 能力经 `AddSeeingModule`」；职责单一、可独立开关 |
| 默认开关 | 默认启用（进入内置能力集 Code/Work/Research/Full/Minimal） | 开箱即用；可经 `Boot` / `CapabilitySets` 关闭 |
| 依赖层 | 仅 `io.local`（`ISubprocessFactory` + `IFileSystem`） | 不耦合 `Seeing.Agent.Tools.Git`；git 缺失时静默 no-op |
| 幂等策略 | `git check-ignore` 探测 + 标记块追加 | 不重复写、不覆盖手写内容、与 `.gitignore` 不打架 |
| 触发时机 | 模块 `ActivateAsync` + `IReloadHandler(WorkspaceChange)` | 启动生效，工作区运行时切换后对新仓库生效 |

**为何不用 `.gitignore`：** 会修改被追踪文件、产生 diff、影响所有协作者，且与用户诉求冲突。
**为何不放 Core：** Core 是零工具/零副作用的瘦脊柱；git/文件系统副作用属于能力层。

## 3. 架构与组件

新增能力包 `src/capabilities/Seeing.Agent.Workspace/`（程序集 `Seeing.Agent.Workspace`）：

| 文件 | 职责 |
|------|------|
| `Seeing.Agent.Workspace.csproj` | 目标 net10.0；仅引用 Abstractions + DI/Logging Abstractions |
| `WorkspaceGitExcludeModule.cs` | `ISeeingModule`，id `workspace.git-exclude`；Activate 应用 + 挂载 Handler，Deactivate 卸 Handler |
| `WorkspaceGitExcluder.cs` | 核心逻辑：解析 git 仓库 → 定位 `info/exclude` → 探测 → 幂等追加 |
| `WorkspaceGitExcludeReloadHandler.cs` | `IReloadHandler`，订阅 `WorkspaceChange`，切换后对新工作区重新应用 |
| `GitExcludeOutcome.cs` | 结果枚举（诊断/日志用） |

**依赖方向：** 能力包 → 仅 `Seeing.Agent.Abstractions`（`IExecutionWorld`/`ISubprocessFactory`/
`IFileSystem`、`IWorkspaceProvider`、`IReloadHandler*`、`ISeeingModule`）。禁止引用 Core。

### 3.1 数据流

```
InitializeSeeingAsync
  → 工作区 Initialize（workspaceRoot 确定）
  → 结算 → Activate 启用模块
      → WorkspaceGitExcludeModule.ActivateAsync
          → WorkspaceGitExcluder.EnsureExcludedAsync(workspaceRoot)
          → IReloadHandlerRegistry.RegisterHandler(reloadHandler)

（运行时）工作区切换 → ReloadOrchestrator 广播 WorkspaceChange
  → WorkspaceGitExcludeReloadHandler.ReloadAsync
      → EnsureExcludedAsync(新 workspaceRoot)
```

### 3.2 `EnsureExcludedAsync` 算法

1. `git -C <workspaceRoot> rev-parse --show-toplevel` → 失败 = 非仓库 → `NotARepository`。
2. `seeingDir = <workspaceRoot>/.seeing`；计算其相对仓库根的路径 `rel`（正斜杠）。
   - `rel` 越出仓库根（以 `..` 开头）→ `OutsideRepository`。
3. `pattern = "/" + rel + "/"`（前导 `/` 锚定仓库根）。
4. `git -C <repoRoot> rev-parse --git-dir` → `excludePath = <repoRoot>/<gitDir>/info/exclude`
   （`git-dir` 为绝对路径时直接拼接；兼容 worktree/submodule）。
5. `git -C <repoRoot> check-ignore -q --no-index -- "<rel>/"`：
   - exit 0（已被任意来源忽略，含 `.gitignore`）→ `AlreadyIgnored`，不写。
6. 读取 `excludePath`；若已含标记块 → `AlreadyManaged`，不写（防重复）。
7. 追加标记块（保留原内容与换行风格）→ `Applied`。

**标记块：**

```
# >>> Seeing.Agent managed ignore >>>
/.seeing/
# <<< Seeing.Agent managed ignore <<<
```

### 3.3 错误处理

| 情况 | 行为 |
|------|------|
| git 不可执行（未安装） | 捕获启动异常 → `Unavailable`，仅 Debug 日志 |
| 非 git 仓库 | `NotARepository`，Debug 日志 |
| 工作区在仓库之外 | `OutsideRepository`，Debug 日志 |
| 读/写 `info/exclude` 失败（权限等） | 捕获 → `Failed`，Warning 日志，不抛出 |
| 超时（15s） | `Failed`，Warning 日志 |

**不阻断启动**：`ActivateAsync` 捕获所有异常并记录，绝不向结算/启动流程抛出。

### 3.4 边界与已知取舍

- **Deactivate 不删除已写入条目**：删除会改动用户本地文件且可能令其意外；保留无害且再次 Activate
  时被探测判为 `AlreadyIgnored` 而幂等跳过。
- **仅处理 `.seeing`**：不引入通用「额外忽略项」配置（YAGNI）。
- **通过 `IFileSystem`（io.local）读写**：遵守「能力包禁止直接 `File.*`」；该能力本质依赖本地 git，
  故 `DependsOn: ["io.local"]`。

## 4. 变更清单

| 位置 | 变更 |
|------|------|
| `src/capabilities/Seeing.Agent.Workspace/` | 新增能力包（4 文件 + csproj） |
| `src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs` | `Minimal/Code/Work/Research/Full` 各加 `workspace.git-exclude` |
| `Seeing.Agent.slnx` | `/src/capabilities/` 与 `/tests/capabilities/` 各加项目 |
| `samples/Seeing.Agent.WebUI`、`Tui`、`Gateway.Server`、`Cli` | `AddSeeingModule<WorkspaceGitExcludeModule>` + ProjectReference |
| `tests/capabilities/Seeing.Agent.Workspace.Tests/` | 新增单元测试 |
| `docs/architecture/02-modules.md` | 模块表新增一行 |

## 5. 测试

- `NotARepository` / `AlreadyIgnored`（文件不变）/ `Applied`（追加块）/ `AlreadyManaged`（不重复）
- 工作区为仓库子目录 → 生成锚定模式 `/sub/.seeing/`
- 工作区在仓库之外 → `OutsideRepository`
- 保留原有内容与换行风格
- 模块元数据（id/依赖）与 `ConfigureServices` 注册
- 实机验证：临时仓库跑真实 git，确认 `git status` 不含 `.seeing`

## 6. 验收标准

1. 在未忽略 `.seeing` 的仓库中启动 → `git status` 不再显示 `.seeing/`，且 `.gitignore` 未被修改。
2. 反复启动 → `info/exclude` 不产生重复块。
3. 已由 `.gitignore` 忽略的仓库（如本仓库）→ `info/exclude` 不被改动。
4. 非 git 目录启动 → 无异常、无写入。
5. `Boot=minimal` 等关闭该模块时不产生任何副作用。
6. 构建 + 相关测试通过；`BuiltInCapabilitySets.Full` 守门测试通过。
