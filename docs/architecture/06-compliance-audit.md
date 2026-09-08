# 06 合规审查

**审查日期：** 2026-09-08（残留清理后复扫）  
**对照：** `docs/superpowers/specs/2026-09-08-modular-architecture-design.md` + 本目录 01–05  
**先前签字：** `.superpowers/sdd/reviews/compliance-remediation-final-r3.md` → Ready to merge: Yes  
**深化说明：** `docs/superpowers/specs/2026-09-08-architecture-docs-deepen-design.md`

本文反映**当前工作树**扫描结果，供后续开发对照，防止回潮。

---

## 总评

| 维度 | 状态 | 证据摘要 |
|------|------|----------|
| Core 零能力包引用 / AddSeeingCore 零工具 | ✅ | 能力包 csproj 无 Core；无 `AddSeeingAgent` |
| 能力包无 Core ProjectReference | ✅ | `rg Core.csproj` **仅** `src/hosting/*`（5 个 Shape/Hosting） |
| 工具 Activate 挂载 | ✅ | `*Module*` 无 `AddSingleton<ITool>` |
| Hosting ↛ Skills / ACP | ✅ | hosting csproj 无 Skills/Acp |
| Gateway ↛ Core / Hosting | ✅ | gateway 下无 Core/Hosting ProjectReference |
| Hosting.Gateway ↛ Agent.Gateway | ✅ | hosting 下无 Agent.Gateway.csproj |
| HostedService 复活 | ✅ | `IModuleHostedService` + Acp/Memory/Scheduler 实现 |
| 连接自管 | ✅ | Acp/Memory Owner；Memory 消费者经 `RequireOpen` 每次取连接 |
| Blazor 全贡献路由 | ✅ | 壳页无 `@page`；ModuleRouter |
| Embed Host Shape | ✅ | `src/hosting/...Embed` + `samples/...Embed.Demo` |
| Core 命名空间 `Seeing.Agent.Core.*` | ✅ | spine 内已迁 |
| 磁盘分类目录 | ✅ | `src/` 顶层仅六类；`tests/` 含镜像 + apps/plugs |
| 能力包互硬引用（Skills/Mcp） | ✅ | Scheduler/Acp 等无 Skills/Mcp ProjectReference |
| schema 单点（执行路径） | ✅ | Native `AgentExecutor` 只读 `context.ToolSchemas`；计算在 `ExecutionJobService` |
| 无阻塞式 GetToolSchemas 同步包装 | ✅ | 已删除 `Task.Run().GetResult()` 同步 API |

**结论：** **架构方向性债已关闭，本轮无需为合规做强制重构。**  
后续风险主要在**回潮**（平铺新项目、ConfigureServices 挂 ITool、能力包再引 Core、配置旁路）。用 [README 扫描命令](README.md) 守门。

---

## 可接受残留 / 技术债（非阻断）

| 项 | 说明 | 建议 |
|----|------|------|
| Hosting 仍引用 Tools.Support | Task/Todo 继承 `ToolBase`；Support 仅 Abstractions，无具体 Tools.* | **保持**：勿上提实现到 Abstractions，勿再引 Tools.FileSystem 等 |
| `GetToolSchemasAsync()` 无参 / ForMode / ForAgent | 诊断与测试枚举注册表；**不得**写回 ChatRequest | 执行路径只用 settled ids 重载 |

### 本轮已清理（2026-09-08）

| 项 | 处理 |
|----|------|
| Memory 构造时 `EnsureInstance` 捕获连接 | 消费者经 `SqliteConnectionSource` → `RequireOpen()`；DI 注入 Owner |
| Quota/TokenTracker `Dispose` 共享连接 | 已移除错误 Dispose |
| `GetToolSchemas*` 同步 + `Task.Run().GetResult()` | 已删除；测试改为 async |

---

## 本次复扫命令与结果

```text
rg "Seeing\.Agent\.Core\.csproj" src -g "*.csproj"
→ 仅 hosting/Hosting{,.Web,.Headless,.Embed,.Gateway}

rg "AddSingleton<\s*ITool" src -g "*Module*.cs"
→ 空

rg "Seeing\.Agent\.(Skills|Mcp)\.csproj" src -g "*.csproj"
→ 空

rg "Seeing\.Agent\.Gateway\.csproj" src/hosting -g "*.csproj"
→ 空

rg "Seeing\.Agent\.(Core|Hosting)\.csproj" src/gateway -g "*.csproj"
→ 空

rg "AddSeeingAgent\b" src samples -g "*.cs"
→ 空

src/ 顶层目录
→ primitives, abstractions, spine, hosting, capabilities, gateway

GetToolSchemas 调用（执行路径）
→ ExecutionJobService 计算；AgentExecutor 读 context.ToolSchemas
→ ToolsCommands 诊断查询 ToolManager（可接受）
```

---

## 是否需要重构？

| 类别 | 判定 |
|------|------|
| 分层 / 模块生命周期 / schema 单点 / 目录分类 | **否**（已合规） |
| Memory 连接经 Owner 每次取、同步 GetToolSchemas 清理 | **已完成**（本轮） |
| Hosting→Tools.Support | **保持**（有意依赖，非债） |
| 文档与入口过时表述 | **已随 architecture 文档集处理** |

若出现下表现象，再开重构 PR，不要用补丁旁路「先顶住」。

---

## 若出现下列现象 → 立即停并对照本文

| 现象 | 可能根因 |
|------|----------|
| 配置禁用模块但 schema 仍有工具 | 又在 ConfigureServices 挂了 ITool，或 Activate 未 Unregister |
| 热重载后工具还在 / 后台还在跑 | Deactivate 未对称；HostedService 未实现复活协议 |
| 改了 seeing.json 节不生效 | 未 `IConfigSectionRegistry.Register` 或旁路读文件 |
| 能力包编译依赖 Core | 违反分层；上提端口到 Abstractions |
| 新项目出现在 `src/Seeing.Agent.Xxx` 根下 | 违反目录分类；移入对应子目录并改 slnx |
| ChatRequest.Tools 与 SchemaSnapshot 不一致 | 第二处计算 schema 写入请求 |

---

## 相关报告

- 合规整改计划：`docs/superpowers/plans/2026-09-08-modular-arch-compliance-remediation.md`  
- 终审 r3：`.superpowers/sdd/reviews/compliance-remediation-final-r3.md`  
- W3 依赖盘点（历史）：`docs/superpowers/plans/2026-09-08-w3-core-dependency-inventory.md`  
- 文档深化设计：`docs/superpowers/specs/2026-09-08-architecture-docs-deepen-design.md`
