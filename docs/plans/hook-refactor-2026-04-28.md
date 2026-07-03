# Hook 系统重构实施计划

**创建时间**: 2026-04-28
**目标**: 统一 Hook 系统，消除重复触发，完善数据契约
**破坏性变更**: Yes - SessionHookManager、MemoryHookPoints 等将被删除

---

## 总览

| 统计 | 数量 |
|------|------|
| 新建文件 | 12 |
| 删除文件 | 10 |
| 修改文件 | 35+ |
| 可并行阶段 | 6 |
| 串行阶段 | 4 |
| 预计工作量 | Medium (3-5 天) |

---

## 架构改进要点

### 1. 统一 Hook 体系
- 合并 HookPoints、SessionHookPoints、MemoryHookPoints 为 HookRegistry
- 合并 HookManager、SessionHookManager 为统一的 HookManager
- 新增 HookPolicy（Blocking/FireAndForget/Parallel）

### 2. 消除重复触发
- AgentExecutor 删除所有 chat/tool/agent Hook 触发
- AgentBase 仅顶层触发 agent.* Hook
- LlmService 负责所有 chat.* Hook
- ToolInvoker 负责所有 tool.* Hook

### 3. 完善数据契约
- HookPayload 增强元数据（Timestamp、TraceId）
- HookDataContract 定义每个 Hook 点的字段契约
- 区分 Blocking（可修改 Mutable）和 FireAndForget（仅通知）

### 4. 优化 Handler 设计
- IHookHandler 单点监听（推荐）
- IMultiHookHandler 多点监听（特殊场景）
- Handler 从 HookPayload.Result 获取数据

---

## Phase 进度

| Phase | 状态 | 说明 |
|-------|------|------|
| Phase 0 | ✅ 完成 | 创建计划文件 |
| Phase 1 | ✅ 完成 | 核心框架创建（12 个新文件） |
| Phase 2 | ✅ 完成 | 删除旧文件（10 个文件） |
| Phase 3 | ✅ 完成 | Agent 层修改 |
| Phase 4 | ✅ 完成 | Session 层修改 |
| Phase 5 | ✅ 完成 | Memory 层修改 |
| Phase 6-9 | ✅ 完成 | Extension/其他/测试/示例修改 |
| Phase 10 | ✅ 完成 | 验证测试（309 个测试全部通过） |

---

## 文件清单

### 新建文件
1. `src/Seeing.Agent/Core/Hooks/HookPolicy.cs`
2. `src/Seeing.Agent/Core/Hooks/HookSpec.cs`
3. `src/Seeing.Agent/Core/Hooks/HookRegistry.cs`
4. `src/Seeing.Agent/Core/Hooks/HookPayload.cs`
5. `src/Seeing.Agent/Core/Hooks/DataField.cs`
6. `src/Seeing.Agent/Core/Hooks/HookDataContract.cs`
7. `src/Seeing.Agent/Core/Hooks/IHookHandler.cs`
8. `src/Seeing.Agent/Core/Hooks/HookResult.cs`
9. `src/Seeing.Agent/Core/Hooks/IHookManager.cs`
10. `src/Seeing.Agent/Core/Hooks/HookManager.cs`
11. `src/Seeing.Agent/Core/Hooks/HookInterruptedException.cs`

### 删除文件
1. `src/Seeing.Agent/Hooks/IHookManager.cs`
2. `src/Seeing.Agent/Hooks/HookManager.cs`
3. `src/Seeing.Agent/Core/Interfaces/IHook.cs`（修改后可能删除）
4. `src/Seeing.Session/Hooks/SessionHookPoints.cs`
5. `src/Seeing.Session/Hooks/SessionHookManager.cs`
6. `src/Seeing.Session/Hooks/ISessionHook.cs`
7. `src/Seeing.Session/Hooks/SessionHookContext.cs`
8. `src/Seeing.Agent.Memory/Core/MemoryHookPoints.cs`

### 主要修改文件
- `src/Seeing.Agent/Core/Abstractions/AgentBase.cs`
- `src/Seeing.Agent/Core/AgentExecutor.cs`
- `src/Seeing.Agent/Core/Models/AgentContext.cs`
- `src/Seeing.Agent/Llm/LlmService.cs`
- `src/Seeing.Agent/Tools/ToolInvoker.cs`
- `src/Seeing.Agent/Rules/RuleEngine.cs`
- `src/Seeing.Session/Management/SessionManager.cs`
- `src/Seeing.Agent.Memory/Integration/MemoryHookHandler.cs`
- `src/Seeing.Agent.Memory/Integration/MemoryExtension.cs`
- `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

---

## 验收标准

1. ✅ 编译成功无错误
2. ✅ 所有单元测试通过
3. ✅ MemoryHookHandler 能正确获取 chat.after_complete 数据
4. ✅ Session Hook 保持异步不阻塞行为
5. ✅ agent.before_invoke 仅顶层触发一次
6. ✅ chat.after_complete 流式版有完整 Result 数据
7. ✅ 没有 Hook 重复触发

---

## 备注

- 详细实施步骤见附件文档
- 代码附件见后续补充