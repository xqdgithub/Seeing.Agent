# Seeing.Agent 架构与开发文档

> **权威规格：** [模块化架构设计](../superpowers/specs/2026-09-08-modular-architecture-design.md)  
> **本文档集：** 日常开发落地规范；与规格冲突时以规格为准，并应提 PR 同步两边。  
> **更新日期：** 2026-09-25（全项目整改：文档漂移修复、接受残留清单、release notes）  
> **深化说明：** [architecture-docs-deepen-design](../superpowers/specs/2026-09-08-architecture-docs-deepen-design.md)

## 新人 5 分钟

1. 读本页「一句话原则」+「宿主启动模板」。  
2. 要改行为 → [04 开发规范](04-development-standards.md) PR 清单。  
3. 要加能力 → [05 扩展指南](05-extension-guide.md)。  
4. 配置不生效 / 热重载怪 → [03 配置·结算·热重载](03-configuration-lifecycle.md) 排障表。  
5. 怀疑分层回潮 → [06 合规审查](06-compliance-audit.md) + 下方扫描命令。

## 阅读顺序

| 文档 | 用途 |
|------|------|
| [01 系统架构总览](01-overview.md) | 分层、Host Shape × Scenario、执行路径、数据一致性 |
| [02 系统模块地图](02-modules.md) | 包职责、模块 id、磁盘路径、Scenario、samples |
| [03 配置·结算·热重载](03-configuration-lifecycle.md) | seeing.json、结算公式、Reload 时序、禁止事项、排障 |
| [04 开发规范与反模式](04-development-standards.md) | 强制约定、PR 检查清单、反模式 |
| [05 扩展指南](05-extension-guide.md) | 新工具包 / 模块 / UI / LLM / Scenario / Gateway |
| [06 合规审查](06-compliance-audit.md) | 当前仓库对照规格的结论与残留 |
| [07 WebUI 统一模型选择](07-webui-model-picker.md) | Session Badge+Modal / 配置页 Dropdown、目录新鲜度 |
| [08 权限授权子系统 Release Notes](08-permission-authorization-release-notes.md) | 破坏性变更清单、行为差异、已知边界 |
| [09 会话工具与会话组 Release Notes](09-session-tools-and-groups-release-notes.md) | 会话组并发/缓存、交接幂等与回滚、已知边界 |
| [10 会话持久化写回 Release Notes](10-session-persistence-release-notes.md) | 写回调度/去抖合并、破坏性契约变更、选项与已知边界 |
| [11 全项目整改 Release Notes](11-remediation-release-notes.md) | 2026-09-25 批次 1–7 行为变更、破坏性契约、接受残留 |
| [源码目录分类设计](../superpowers/specs/2026-09-08-source-layout-design.md) | `src/`/`tests/` 分类落地说明 |
| [Gateway 总览](../gateway/README.md) | Gateway 族路径与组合规则 |

## 一句话原则

1. **编译期引用 = 进程目录（available）**；**配置启用 = enabled 子集**。目录外 id 告警忽略。  
2. **Core 是瘦脊柱**：零工具实现、零能力包 ProjectReference、不 `AddSeeingAgent`。  
3. **能力包只依赖 Abstractions**（+ 原语 / Tools.Support）；禁止引用 Core 具体类型。  
4. **schema 只在 `ExecutionJobService` 算一次**；executor 读 `context.ToolSchemas`。  
5. **工具在 Activate 挂载、Deactivate 卸载**；连接与 HostedService 与模块生命周期对称。  
6. **进程场景改导航/激活；会话场景只收窄下一次 Submit**；热重载 Deactivate 默认等在途结束。  
7. **单一真相源**：enabled 模块集、ToolManager 注册表、UI 贡献、HostedService 门闩必须同源结算结果，禁止旁路第二套开关。

## 磁盘目录（程序集名不变）

```
src/
  primitives/      Session, TokenEstimation, ConfigSchema
  abstractions/    Seeing.Agent.Abstractions
  spine/           Seeing.Agent.Core
  hosting/         Hosting + Web / Headless / Embed / Gateway / Tui Shape
  capabilities/    Tools.*, Skills, Mcp, Llm.*, Agents.BuiltIn,
                   Scheduler, Memory, Acp, TokenBudget, IO.Local
  gateway/         Seeing.Gateway*, Seeing.Agent.Gateway
tests/
  primitives|spine|hosting|capabilities|gateway/   # 与 src 镜像
  apps/            WebUI.Tests, Cli.Tests, Tui.Tests
  plugs/           Provider.*Tests
samples/           WebUI, Tui, Gateway.Server, Cli, Embed.Demo, …
plugs/providers/   DeepSeek, OpenCodeZen
```

`.slnx` 使用同名虚拟 Folder。新增项目必须放进对应分类目录，禁止再平铺回 `src/` 根下。

## 宿主启动模板（必遵）

```csharp
var registry = new ConfigSectionRegistry();
services.AddSingleton<IConfigSectionRegistry>(registry);

// 1) 能力模块先于 Core
services.AddSeeingModule<LocalExecutionWorldModule>(registry);
services.AddSeeingModule<FileSystemModule>(registry);
// … Skills / Mcp / Llm.* / AgentsBuiltIn / Scheduler …

services.AddSeeingCore(registry);

var host = builder.Build();
await host.Services.InitializeSeeingAsync(); // Host.Start 之前
await host.RunAsync();
```

**禁止：** `AddSeeingAgent`、在 Core 里硬编码装载能力包、在 `appsettings.json` 写 SeeingAgent 节、在 `ConfigureServices` 里 `AddSingleton<ITool>`。

## 防回归扫描（PR 可粘贴）

在仓库根执行（PowerShell / bash 均可；需已安装 `rg`）：

```bash
# 能力包不得引用 Core（仅 hosting/* 允许）
rg "Seeing\.Agent\.Core\.csproj" src -g "*.csproj"

# Module 不得在 ConfigureServices 挂 ITool
rg "AddSingleton<\s*ITool" src -g "*Module*.cs"

# Hosting.Gateway 不得拉 Gateway 集成包
rg "Seeing\.Agent\.Gateway\.csproj" src/hosting -g "*.csproj"

# Gateway 族不得拉 Core / Hosting
rg "Seeing\.Agent\.(Core|Hosting)\.csproj" src/gateway -g "*.csproj"

# 禁止已删除的聚合入口
rg "AddSeeingAgent\b" src samples -g "*.cs"

# 权限契约分层：旧通道/中间件/白名单/缓存类型不得回归
# （允许注释中的历史说明，如 IWorkspaceWhitelist→IPermissionGrantStore）
rg "SerializingPermissionChannel|DynamicPermissionChannel|DefaultPermissionChannel|IPermissionMemory|SessionPermissionMemory|IWorkspaceWhitelist|SessionWorkspaceWhitelist|IPermissionCache|PermissionCacheKey|PermissionMiddleware|IPermissionEventSink|PermissionChannelResult|BlazorPermissionChannel|PermissionRunContext|SetRunContext|PermissionResponseEvent" src samples tests

# Core 零工具实现：`[Tool(` 应为空；`: ITool\b` 仅剩装饰器/反射基础设施（ToolDecorator/ReflectedTool）
rg ": ITool\b|\[Tool\(" src/spine

# 能力包内裸 IHostedService 注册（应为零：长驻执行循环须实现 IModuleHostedService）
rg "AddHostedService<" src/capabilities -g "*.cs"

# 新项目勿落在 src 根平铺
# （src 下应仅有 primitives|abstractions|spine|hosting|capabilities|gateway）
```

期望：`Core.csproj` 命中仅 `src/hosting/*`；其余命令无命中（或仅文档注释）。权限契约扫描如有命中，须为历史注释说明而非类型引用。`rg ": ITool\b|\[Tool\(" src/spine` 预期：`[Tool(` 空、`: ITool\b` 仅装饰器/反射基础设施；`rg "AddHostedService<" src/capabilities` 预期：无命中（注册型资源随 Module 生命周期收编，长驻循环改用 `AddModuleHostedService<T>`）。
