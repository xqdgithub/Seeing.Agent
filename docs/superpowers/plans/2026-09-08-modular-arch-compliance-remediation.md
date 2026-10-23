# 模块化合规债重构计划

**创建日期：** 2026-09-08  
**分支：** `feature/modular-architecture`  
**对照规格：** `docs/superpowers/specs/2026-09-08-modular-architecture-design.md`  
**对照审查：** `docs/architecture/06-compliance-audit.md`  
**范围：** P0 + P1 + P2 全量（用户决策：all）

## 用户决策（已确认）

| 决策点 | 选择 |
|--------|------|
| Gateway 集成包定位 | **strict** — 集成包也只依赖 Abstractions |
| 工具 Activate 迁移节奏 | **bigbang** — 一次性全迁，配不变量测试 |
| SkillCommands 归属 | **both** — 命令移到 Skills 模块 + Hosting 只用接口 |
| 本轮范围 | **all** — P0 + 全部 P1 + P2 |
| HostedService 再激活 | **auto** — 补「复活」协议，Activate 自动重启循环 |
| Embed Host Shape | **now** — 本轮实现 |
| Blazor @page 双轨 | **all_contrib** — 移除壳页 @page，全部走 ModuleRouter/registry |

## 执行顺序（依赖链）

```
Wave 1 (基础设施)
  W1-1 IToolManager.Unregister API
  W1-2 模块 Activate 上下文（IServiceProvider 注入）
  W1-3 ToolManager 移除 GetServices<ITool> 自动注册
  W1-4 ICommandRegistry 扫描机制（供 Skills 命令外迁）
Wave 2 (P0 主体，bigbang)
  W2-1 10 个模块工具迁 Activate 挂载（一次性）
  W2-2 不变量测试：未启用模块 → schema/GetTools 不含
  W2-3 热重载对称测试：Deactivate→消失；再 Activate→恢复
Wave 3 (去 Core 引用，按包切 PR)
  W3-1 TokenBudget / W3-2 Skills / W3-3 Memory
  W3-4 Scheduler / W3-5 Acp
  W3-6 Gateway + Gateway.QQ + Gateway.WeCom（strict）
Wave 4 (Hosting 去 Skills 硬引用)
  W4-1 SkillCommands 移到 Skills 模块
  W4-2 Hosting 改用 ISkillManager 接口
  W4-3 Hosting.csproj 删 Skills ref
Wave 5 (P1 行为债)
  W5-1 HostedService 复活协议（auto）
  W5-2 连接 Activate 自管（Acp/Memory）
  W5-3 Blazor @page 全贡献路由
  W5-4 Gateway Shape 削薄验证
  W5-5 Core 测试独立 → Seeing.Agent.Invariants.Tests
  W5-6 命名空间迁移 Seeing.Agent.Core.*
  W5-7 Embed Host Shape 实现
Wave 6 (P2 卫生)
  W6-1 删 src/Seeing.Agent/obj 空壳
  W6-2 README/specs 同步卫生
  W6-3 PR 模板勾选项
```

每个 Wave 结束跑全量回归（`dotnet build` + `dotnet vstest` 已构建 dll）。

---

## Wave 1 — 基础设施

### W1-1 IToolManager.Unregister API

**现状：** `IToolManager` 只有 `RegisterTool` / `RegisterToolAsync`，无 Unregister。  
**目标：** 补对称卸载 API。

**步骤：**
1. `src/Seeing.Agent.Abstractions/Tools/IToolManager.cs` 增：
   - `bool UnregisterTool(string id)`
   - `Task<bool> UnregisterToolAsync(string id, CancellationToken ct = default)`
2. `src/Seeing.Agent.Core/Tools/ToolManager.cs` 实现：
   - 从内部 `_tools` 字典移除该 id
   - 触发工具集变更通知（供 schema 失效）
   - 返回是否实际移除
3. 单测：注册→Unregister→`GetTools()` 不含；未注册 id → 返回 false

**验收：** 编译通过；新增单测绿。

### W1-2 模块 Activate 上下文

**现状：** `ISeeingModule.ActivateAsync` 无 `IServiceProvider` 入参；模块拿不到 DI 服务去 `RegisterTool`。  
**目标：** Activate 能解析 DI 服务。

**决策：** 改 `ISeeingModule` 接口签名（dev 阶段，可破坏改）。

**步骤：**
1. `ISeeingModule.ActivateAsync` 增参 `IServiceProvider services`（或定义 `ModuleActivateContext`）
2. `DeactivateAsync` 同样增参
3. 所有 16 个模块实现更新签名
4. `SettlementEngine` / `ModuleSettlementReloadHandler` 调用处传入 `IServiceProvider`

**备选（非破坏）：** 模块构造时注入 `IServiceProvider`。但模块实例可能早于 scope。选破坏改更干净。

**验收：** 全模块编译；Activate 能 `GetRequiredService<IToolManager>()`。

### W1-3 ToolManager 移除自动注册

**现状：** `ServiceCollectionExtensions.cs:523-534` `ToolManager` 构造里 `sp.GetServices<ITool>()` 全注册。  
**目标：** 构造时不自动注册；靠模块 Activate。

**步骤：**
1. 删除 `var tools = sp.GetServices<ITool>();` 及 foreach 注册块
2. 保留 `ToolManager` 空构造
3. 此改动与 W2-1 同 PR（否则启动后无工具）

**风险：** 若 W2 未完成即提交，启动后零工具。**必须与 Wave 2 同 PR。**

### W1-4 ICommandRegistry 扫描机制

**现状：** `SkillCommands` 在 Hosting 里硬编码注册。  
**目标：** 命令可由模块贡献，Hosting 经 `ICommandRegistry` 扫描。

**步骤：**
1. 确认 `ICommandRegistry` 是否存在；若无，在 Abstractions 定义
2. Hosting 的 `InitializeCommands` 改为从 registry 扫描所有 `ICommand` 贡献
3. 供 W4-1 使用


---

## Wave 2 — 工具 Activate 挂载（bigbang）

### W2-1 10 模块迁移

**统一模式（每模块）：**

```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddSingleton<XTool>();        // 具体类型工厂，不再 AddSingleton<ITool, X>
}

public async Task ActivateAsync(IServiceProvider sp, CancellationToken ct)
{
    _ui?.Register(this);
    var tm = sp.GetRequiredService<IToolManager>();
    await tm.RegisterToolAsync(sp.GetRequiredService<XTool>(), ct);
}

public async Task DeactivateAsync(IServiceProvider sp, CancellationToken ct)
{
    var tm = sp.GetRequiredService<IToolManager>();
    foreach (var id in ProvidedTools)
        await tm.UnregisterToolAsync(id, ct);
    _ui?.Unregister(Id);
}
```

**模块清单（按 ProvidedTools 数排序）：**

| 模块 | 工具 | 当前挂载点 |
|------|------|-----------|
| Basic | current_time | BasicModule.cs:48 |
| Skills | skill | SkillsModule.cs:79 |
| Shell | bash | ShellModule.cs:34 |
| Git | git_status/diff/log/commit | GitModule.cs:42-45 |
| Web | web_fetch/search/code_search | WebModule.cs:37-43 |
| Acp | acp/acp_status | AcpExt.cs:84-85 |
| Memory | memory_search/write/read | MemoryExt.cs:189-191 |
| Scheduler | cron_list/create/delete/disable/resume/run | SchedulerExt.cs:42-46 |
| Hosting | task/task_status/todo_write | HostingExt.cs:51-64 |
| FileSystem | read/write/edit/glob/grep/add_workspace/delete | FileSystemModule.cs:42-48 |

**步骤：** 逐模块按模式改；与 W1-2/W1-3 同 PR。

### W2-2 不变量测试

**新增** `tests/Seeing.Agent.Tests/Modules/ToolActivationInvariantTests.cs`：
- 启动只 `AddSeeingCore` → `GetTools()` 为空
- 启用 minimal scenario → 只含 minimal 模块工具
- 禁用某模块 → schema 与 `GetTools()` 均不含其 `ProvidedTools`
- `ProvidedTools` 与实际注册 id 一致（防漂移）

### W2-3 热重载对称测试

- Deactivate `filesystem` → `read` 工具消失；再 Activate → 恢复
- 在途执行时 Deactivate → 推迟（验证 `ForceCancelInFlight` 路径）


---

## Wave 3 — 去 Core 引用（按包切 PR）

### 通用步骤（每包）

1. `rg "using Seeing.Agent" src/<pkg>` 列出 Core 命名空间使用点
2. 被用类型逐个上提到 Abstractions：
   - Options 类 → Abstractions 定义接口 `IXxxOptions`，Core 实现 `IOptionsMonitor<IXxxOptions>`
   - 具体服务 → 抽接口到 Abstractions，Core 实现
3. csproj：删 `Seeing.Agent.Core.csproj` ref，确保有 `Seeing.Agent.Abstractions.csproj`
4. `dotnet build src/<pkg>` + 该包测试
5. 不变量：`rg "Seeing.Agent.Core.csproj" src/<pkg>` 为空

### W3-1 TokenBudget

**Core 依赖点：** `SeeingAgentOptions`（TokenBudget 配置节）。  
**上提：** `ITokenBudgetOptions` 到 Abstractions；Core 的 `SeeingAgentOptions` 实现该接口或 Adapter。

### W3-2 Skills（配合 W4）

**Core 依赖点：** `SeeingAgentOptions`、`ConfigSectionOptionsMonitor<>`、`SkillManager` 已有 `ISkillManager`。  
**上提：** `ISkillsOptions`；`ConfigSectionOptionsMonitor<>` 移到 Abstractions（多包共用）。

### W3-3 Memory

**Core 依赖点：** `IWorkspaceWhitelist`、options。  
**上提：** `IWorkspaceWhitelist` 已在 Abstractions？确认；options 上提。

### W3-4 Scheduler

**Core 依赖点：** options + Hosting 依赖（Job 调度回调用 Hosting 类型）。  
**上提：** 调度回调接口到 Abstractions；Hosting 反向不依赖（Hosting 是上层）。

**注意：** Scheduler 可能需要 Hosting 的 `IAgentExecutor` —— 该接口已在 Abstractions。验证不反向。

### W3-5 Acp

**Core 依赖点：** `IAgentExecutor`（已在 Abstractions）、options。  
**上提：** options 接口化。

### W3-6 Gateway + QQ + WeCom（strict）

**决策 strict：** 集成包也只依赖 Abstractions。  
**Core 依赖点：** Gateway 集成需调 Core 的 agent 执行/会话。  
**上提：** 所需执行/会话端口到 Abstractions（多数已有）。  
**风险：** Gateway 集成包较重，可能需较大上提。**先扫依赖面，若过大回用户确认拆分。**


---

## Wave 4 — Hosting 去 Skills 硬引用

### W4-1 SkillCommands 移到 Skills 模块

**现状：** `src/Seeing.Agent.Hosting/Commands/SkillCommands.cs` 用 `SkillManager` 具体类。  
**步骤：**
1. 移文件到 `src/Seeing.Agent.Skills/Commands/`
2. 改用 `ISkillManager`（Abstractions）
3. Skills 模块 `ActivateAsync` 经 `ICommandRegistry` 贡献命令
4. Hosting `InitializeCommands` 从 registry 扫描（W1-4）

### W4-2 Hosting 改用接口

**现状：** `HostingServiceCollectionExtensions.cs:131` `services.GetService<SkillManager>()`。  
**步骤：** 改 `ISkillManager`；移除 `using Seeing.Agent.Skills`。

### W4-3 csproj 删 ref

**步骤：** `Hosting.csproj` 删 `Seeing.Agent.Skills.csproj` ref。  
**验收：** `dotnet build src/Seeing.Agent.Hosting` 通过；Hosting 不再带 Skills 程序集。

---

## Wave 5 — P1 行为债

### W5-1 HostedService 复活协议（auto）

**目标：** Deactivate 后再 Activate 自动重启循环。  
**步骤：**
1. 定义 `IModuleHostedService` 接口（Abstractions）：`StartAsync`/`StopAsync` + `IsRunning`
2. 长驻服务（Acp/Memory/Scheduler）实现该接口
3. `ModuleSettlementReloadHandler`：
   - Activate 时若服务 `!IsRunning` → `StartAsync`
   - Deactivate 时 `StopAsync`（翻标志 + 唤醒 Channel）
4. 再 Activate → 检查 `!IsRunning` → 重启
5. 测试：Activate→Deactivate→Activate 循环验证服务复活

### W5-2 连接 Activate 自管

**目标：** Acp/Memory 连接生命周期与模块对称。  
**步骤：**
1. 连接对象从 DI Singleton 改为模块字段
2. Activate 建连，Deactivate 断连
3. 配合 W5-1 的 HostedService 协议
4. 测试：Deactivate → 连接断；再 Activate → 重连

### W5-3 Blazor @page 全贡献路由

**决策 all_contrib：** 移除壳页 `@page`，全部走 ModuleRouter/registry。  
**步骤：**
1. 扫 `samples/Seeing.Agent.WebUI` 所有 `@page "/..."` 指令
2. 内置页改为经 `IUiContributionRegistry` 贡献 `RouteContribution`
3. 移除 AppAssembly 扫描 fallback（`ModuleRouter` 已是唯一源）
4. 验证所有路由可达；未启用模块路由 → `NotEnabledFragment`
5. 测试：路由表只来自 registry

### W5-4 Gateway Shape 削薄验证

**步骤：** 配合 W3-6。验证 `Seeing.Agent.Hosting.Gateway.csproj` 不直接 ref Scheduler/Tools；集成包由 sample 组合。

### W5-5 Core 测试独立

**目标：** Core 测试不扫能力包。  
**步骤：**
1. 新建 `tests/Seeing.Agent.Invariants.Tests`
2. 把 `tests/Seeing.Agent.Tests` 中依赖 `Tools.*` 的测试迁过去
3. Core 测试只 ref `Seeing.Agent.Core` + `Abstractions`
4. 不变量测试（W2-2/W2-3）放 Invariants.Tests

### W5-6 命名空间迁移

**目标：** `Seeing.Agent.*` → `Seeing.Agent.Core.*`（与程序集名一致）。  
**步骤：**
1. 按子命名空间分批（Configuration/Tools/Hooks/Permission/…）
2. IDE 全局 rename，每批一 PR
3. 低优先级，可与其它 Wave 并行

### W5-7 Embed Host Shape

**决策 now：** 本轮实现。  
**步骤：**
1. 确认规格中 Embed Shape 的契约（无 UI、进程内嵌入）
2. 新建 `src/Seeing.Agent.Hosting.Embed`（不 ref Web/Headless）
3. 提供 `AddSeeingEmbedHost` 入口
4. sample：文档示例（仓库内不再内置 Embed demo）
5. 验证：不拉 Web 程序集；可独立初始化 agent 执行


---

## Wave 6 — P2 卫生

### W6-1 删空壳

- 删 `src/Seeing.Agent/obj`（若存在）；确认 `src/Seeing.Agent/` 无残留

### W6-2 文档同步

- README 特性列表保持模块化口径
- `docs/architecture/06-compliance-audit.md` 每完成一项更新状态

### W6-3 PR 模板

- 新增 `.github/pull_request_template.md`（若无）勾选项：
  - [ ] 未在 Core 加工具/能力包引用
  - [ ] 工具经 Activate 挂载
  - [ ] 配置节经 `IConfigSectionRegistry` 登记
  - [ ] specs 与 architecture 同步

---

## 验收不变量（全部完成后应全绿）

- [ ] `AddSeeingCore` only → 零 `ITool`
- [ ] 未启用模块 → schema 与 `GetTools()` 均不含其 `ProvidedTools`
- [ ] Deactivate → 工具与 UI Nav 消失；再 Activate 恢复
- [ ] HostedService 再 Activate → 循环复活
- [ ] `rg "Seeing.Agent.Core.csproj" src -g "*.csproj"` 仅 Hosting/Host Shape
- [ ] `rg "AddSingleton<ITool" src -g "*Module*.cs"` 为空
- [ ] Blazor 路由表只来自 `IUiContributionRegistry`
- [ ] Embed Host Shape 可独立初始化
- [ ] 配置只经 `IConfigSectionRegistry` 登记节读写

---

## 审查方法（可重复）

```bash
# Core 不得引用能力包
rg "ProjectReference" src/Seeing.Agent.Core/Seeing.Agent.Core.csproj

# 能力包不得引用 Core（目标态应为空，Hosting*/Host Shape 除外）
rg "Seeing.Agent.Core.csproj" src -g "*.csproj"

# ConfigureServices 直接挂 ITool（应为空）
rg "AddSingleton<ITool" src -g "*Module*.cs"

# Hosting 不得引用 Skills
rg "Seeing.Agent.Skills.csproj" src/Seeing.Agent.Hosting/Seeing.Agent.Hosting.csproj
```

---

## 风险与回滚

| Wave | 主要风险 | 回滚策略 |
|------|----------|----------|
| W1+W2 | 启动后零工具（若 Activate 未全迁） | 同 PR；本地 `dotnet run --project samples/Seeing.Agent.WebUI` 验证工具可见 |
| W3 | 上提类型破坏 Core 公共 API | dev 阶段可破坏；保留旧类型作 [Obsolete] 一轮 |
| W3-6 | Gateway 集成包依赖面过大 | 扫描后回用户确认拆分粒度 |
| W5-1 | 复活协议引入新死锁 | 复活测试 + 在途边界测试 |
| W5-3 | 移除 @page 导致路由 404 | 路由可达性测试 |

## PR 切分建议

| PR | 内容 | 预估 |
|----|------|------|
| PR1 | W1 + W2（基础设施 + 10 模块 Activate + 不变量测试） | 大 |
| PR2 | W3-1 TokenBudget 去 Core | 小 |
| PR3 | W3-2 Skills 去 Core（配合 W4） | 中 |
| PR4 | W3-3 Memory 去 Core | 中 |
| PR5 | W3-4 Scheduler 去 Core | 中 |
| PR6 | W3-5 Acp 去 Core | 中 |
| PR7 | W3-6 Gateway/QQ/WeCom 去 Core | 大（可能拆 3 个） |
| PR8 | W4 Hosting 去 Skills | 中 |
| PR9 | W5-1 + W5-2 复活协议 + 连接自管 | 中 |
| PR10 | W5-3 Blazor 全贡献路由 | 中 |
| PR11 | W5-5 Core 测试独立 | 小 |
| PR12 | W5-7 Embed Host Shape | 中 |
| PR13 | W5-6 命名空间迁移（分批） | 小×N |
| PR14 | W6 卫生 | 小 |

每个 PR 跑全量回归（`dotnet build` + `dotnet vstest` 已构建 dll）。




