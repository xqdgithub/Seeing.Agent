# 18 Gateway 请求/应答实时订阅与会话 schema 解析端口 Release Notes

**日期：** 2026-10-17
**相关模块：** `Seeing.Agent.Abstractions`（Events / Execution 契约）、`Seeing.Agent.Core`（ExecutionEventPublisher / LiveEventSubscription）、`Seeing.Gateway`（GatewayAssistantReplyCollector）、`Seeing.Gateway.Client`（SessionEventPump）、`Seeing.Agent.Gateway`（GatewayOrchestratorV2 / GatewayWebSocketHandler）、`Seeing.Agent.Hosting`（SessionToolSchemaResolver / ExecutionRunner）、`Seeing.Agent.Scheduler`（AgentJob / HeartbeatJob）

---

## 1. 概要

修复 2026-10-16 Gateway 重构部署后 QQ 渠道大面积异常的两个代码级根因：

| 根因 | 现象 | 修复 |
|------|------|------|
| RC1 交互回复污染 | 真实回复不送达，秒回 `⚠️ user` / `(空回复)` | 请求/应答**实时订阅三件套**（§2） |
| RC2 定时任务输出垃圾 | 每小时 cron 群发原始工具调用文本 / 臆造时间 | 会话 **schema 结算共享端口**（§3） |
| RC3 顺序多轮毒帧串轮 | 启动后第一条正常，其后全部秒回 `(空回复)` | **按 submit requestId 关联的订阅消费**（§6） |

前两个根因都源于重构引入的语义缺口：RC1 是订阅时序（回放 + 竞态），RC2 是 schema 单点纪律（直连执行旁路绕过结算）；RC3 是客户端共享入站队列的轮次关联缺口（帧关联机制已存在但未被利用）。

## 2. RC1：请求/应答实时订阅三件套

### 2.1 根因链

1. 会话级订阅 + `ExecutionEventPublisher` 环形缓冲**全量回放**（默认 100 条）；
2. WS `HandleSubmitAsync` 先 `SubmitAsync`（投递即唤醒执行）后订阅 → 回放收到**上一轮残留终态**（`LoopCancelled(reason="user")` 等）；
3. `GatewayAssistantReplyCollector` 无轮次概念 → 陈旧终态直接判死本轮（`⚠️ user` / `(空回复)`）。

三件缺一不可（分析证明）：

- 只修「发布器实时订阅」：消灭回放中的陈旧 LoopStart/LoopComplete，但**run 在泵注册前启动**的竞态仍在（LoopStart 漏收 → 门控失效）；
- 只修「泵级注册时序」：不回放后仍可能收到**上一轮在途实时尾流**的陈旧 LoopStart，门被错误打开。

### 2.2 三件套

1. **发布器实时订阅**（无回放）
   - 契约：`IExecutionEventPublisher.SubscribeLive(string sessionId)` → `ILiveEventSubscription`（`Abstractions/Events/ILiveEventSubscription.cs`，`IAsyncEnumerable<IMessageEvent>` + 幂等 `Dispose`）。
   - spine 实现：`ExecutionEventPublisher.SubscribeLive` 与 `Publish`（缓冲写入 + 扇出）**同锁注册**——注册点先于其后任何 `Publish`；`Dispose` 同锁注销 + writer 收口（收口后 buffered 事件仍可读尽）。
   - 非法 sessionId / Null 发布器 → `LiveEventSubscription.Completed`（已收口空订阅）。
   - **live 流仅在 `Dispose` 后终结**（消费方完成枚举后注销），无 idle 收口。

2. **泵级 `BeginLive`**（先注册，后投递）
   - `SessionEventPump.BeginLive(sessionId, ILiveEventSubscription)` → `LiveSessionEventStream`（`StreamAsync(ct)` + 幂等 `Dispose`）；内部共享 `StreamUntilIdleAsync` 核心（internal）。
   - `GatewayOrchestratorV2.BeginReplyStream(sessionId)`：呈现端 + runTracker + 实时订阅**同步注册**后返回。
   - WS `HandleSubmitAsync` 重排为：**① `BeginReplyStream`（注册）→ ② `SubmitAsync`（投递唤醒）→ ack → ③ 流枚举 + finally `Dispose`**。
   - 竞态消除：注册在投递唤醒前同步完成 → 本轮 `LoopStart` 必在门内。

3. **Collector 轮次门控**
   - `GatewayAssistantReplyCollector` 在首个 **fresh `LoopStart`**（`Object=Response, Status=InProgress, SourceType=LoopStart`）之前，一切事件返回 `Ignored`。
   - 消灭上一轮在途实时尾流（含其终态）污染本轮判定。

### 2.3 语义边界

- **legacy `SessionEventPump.StreamAsync`（回放）语义不变**：SSE 端点 / WebUI 流继续回放。
- `AddInProcessSessionClient` 无生产调用方，保持回放路径（已知边界）。
- 生产桥（QQ / WeCom 扩展）全走 WS 实时路径。

## 3. RC2：会话 schema 结算共享端口

### 3.1 根因

commit `b96bfcc` 后 `IAgentExecutor` 只读 `context.ToolSchemas`；`AgentJob` / `HeartbeatJob` 直连 `IAgentExecutor` 的 `AgentContext` **从不设置 ToolSchemas** → LLM 请求无 tools → 模型靠文本「表演」工具调用，输出原始 `date` 命令文本等垃圾。

### 3.2 端口设计（schema 单点纪律保持）

- **契约**（`Abstractions/Execution/ISessionToolSchemaResolver.cs`）：两个重载——`ResolveAsync(sessionId, agent, ct)`（能力包 Scheduler 用，按 id 加载）与 `ResolveAsync(SessionData, agent, ct)`（主执行链用，复用已加载会话实例）→ `SessionToolSchemaResolution(ToolSchemas, SectionIds)`（record + `Empty`）。能力包（Scheduler）只依赖 Abstractions ✓。
- **实现唯一在 Hosting**（`SessionToolSchemaResolver`）：
  - `ResolveAsync`（公开端口）：`sessionId` 版 `Get → LoadAsync → EnsureSessionAsync` 加载后结算；`SessionData` 版直接复用传入实例（避免读到未落盘前的旧状态）。
  - `SettleAsync(session, agent, services)` + `MergeDynamicToolIds`——**已降为 private 核心**，仅由两个公开重载调用，杜绝绕过端口直取内部结算。
- **`ExecutionRunner` 依赖接口化**：ctor **必需注入 `ISessionToolSchemaResolver`**（删除 `new SessionToolSchemaResolver` 回退分支；缺依赖构造期 `ArgumentNullException` 失败，杜绝第二套结算路径与对具体类型的耦合）；`BuildExecutionContextAsync` 改调 `_schemaResolver.ResolveAsync(session, agentDef)` 端口方法；随回退移除而一并删除已死参数 `IOptionsMonitor<SeeingAgentOptions>` / `IScenarioCatalog`（原仅服务于回退构造）。
- **`AgentJob` / `HeartbeatJob` 接入**：`ResolveToolSchemasAsync`（Scheduler 内共享 internal static 助手）——未注册 resolver → 警告 + null 空集继续（不阻断任务）；解析失败 → 警告 + null，不阻断。
- `ChatExecutionContext` 删除无人读取的 `Settlement` 属性（`SessionSettlement` / `SessionSettlementSnapshot` 留在 Hosting internal，被 resolver 使用）。

### 3.3 schema 单点纪律新表述

> schema 只在 `SessionToolSchemaResolver`（Hosting）计算一次；主执行链（`ExecutionRunner`）与 Scheduler 直连执行共用同一结算出口；executor 读 `context.ToolSchemas`。

## 4. 测试

| 套件 | 内容 | 结果 |
|------|------|------|
| `GatewayAssistantReplyCollectorTests` | 门控：LoopStart 前一律 Ignored、陈旧噪声后 fresh 轮只收 fresh 回复 | 7/7 |
| `ExecutionEventPublisherLiveTests` | 实时不漏 / 不回放 / Dispose 注销幂等 / 非法 sessionId | 4/4 |
| `SessionEventPumpLiveTests` | `BeginLive`：注册时序、收口读尽、幂等 Dispose | 3/3 |
| `SessionToolSchemaResolverTests` | scenario=code 结算（含 git_status/read/current_time、不含 memory_search）、DeniedTools 过滤、无 catalog 空、会话缺失 EnsureSession | 4/4 |
| `AgentJobToolSchemasTests` | 经 Quartz JobDataMap 走完整 `AgentJob.Execute`：schema 传到位 / 未注册兜底 null | 2/2 |
| `RemoteSessionClientSequentialExchangeTests` | RC3 回归：伪 WS 服务端三轮顺序桥式交换（RunCompleted 提前 return），三轮各收自己的回复 | 1/1 |
| 回归 | spine（SchemaSnapshot / Compaction / CommandShortCircuit / Instruction / PermissionAuthorizer 53）、Gateway / QQ / Comms.Integration / Invariants 19、e2e 6 | 全绿 |

`Full_CapabilitySet_Should_Cover_All_Implemented_Modules` 守门失败（既有遗漏，通联模块未登记）：`BuiltInCapabilitySets.Full` 补登 `comms` / `channel-messaging`。

## 5. 已知边界

| 边界 | 说明 |
|------|------|
| `AddInProcessSessionClient` 回放路径 | **无生产调用方；若任何宿主启用它，RC1 门控会被上一轮回放的 LoopStart 误开并发送陈旧回复——启用前必须先补实时路径（硬前置条件）** |
| agent 经 `send_channel_message` 自送 | 可能与桥回复重复（既有边界） |
| 共享 WS 客户端队列并发交叉串扰 | **同轮次内已由 requestId 关联过滤缓解**（他轮帧 / 跨会话帧 / 无关联帧一律跳过）；同队列**并发多轮交换**（A 提交未收口又来 B）语义边界仍在——桥按会话串行驱动（每会话同一时刻一轮），多会话并发安全，但桥侧多会话同时 RunCompleted 的提前 return 仍各自滞留尾部帧（已被下轮关联过滤消化） |
| SSE 端点回放语义 | 继续回放（UI 语义），不迁移到 live |
| live 流无 idle 收口 | 仅 `Dispose` 终结；消费方必须在枚举完成后 `Dispose`（泵已保证） |
| Scheduler 宿主未注册 resolver | 警告 + 空 schema 继续（模型无法真实调用工具），任务不中断 |
| Collector 门控吞掉「先于 fresh LoopStart 的本轮真实错误」 | 门控粒度是事件而非轮次：生产者在 `LoopStart` 之前直接 yield 的 `ErrorEvent`（如 agent 被禁用分支、极早期取消）会被判 `Ignored` → 桥侧流结束发空文本。触发面窄（禁用 agent 的 WS 提交 / 极早取消）；接受为已知边界，如需覆盖可演进「终态在门关闭时挂起」语义 |

## 6. RC3：顺序多轮交换陈旧 execution.complete 毒帧（QQ「只有启动后的第一次有回复」）

RC1 三件套部署后仍复现的残余缺陷（远程 Linux ChannelHost 实测：`17:29:21 你是 → 正常回复；17:29:28 好的 → (空回复)；17:29:37 天气 → (空回复)`——启动后第一条成功，其后全部空回复，100% 确定性）。

### 6.1 根因链（客户端侧结构性缺陷）

1. `WebSocketGatewayClient._inboundQueue` 是**单连接共享 FIFO 队列**（进程内 WS 客户端唯一入站通道）；
2. 桥（QQ / WeCom）在 collector 判定 `RunCompleted` 时**提前 return 弃置订阅枚举**——服务端随本轮流收口下发的**尾部 `execution.complete` 帧滞留队列成为毒帧**；
3. 下一条消息的 `SubscribeAsync` 首先消费陈旧 `ExecutionComplete` → 立即 `yield break` → collector 无终态、Text 空 → 桥 `if (!reply.IsTerminal) SendVisibleAsync("")` → `(空回复)` 秒回；
4. 该失败又把**本轮自己的**事件 + `execution.complete` 留成下一轮毒帧（成功 → 空 → 滞后一轮陈旧回复 → 空 → …）。
5. 服务端 `HandleSubmitAsync` 的全部 per-submit 帧（ChatEvent / ExecutionComplete）**本就携带 `frame.Id`（= submit 帧 requestId），客户端从未利用**——`RemoteSessionClient.SubscribeAsync` 终止于**任意** `ExecutionComplete`（不按轮次关联过滤）是缺陷核心。

### 6.2 修复：按 submit requestId 关联的订阅消费

| 层 | 变更 |
|------|------|
| 协议 `GatewaySubmitAckPayload` | 新增 `RequestId`（服务端 ack 时回填 `frame.Id`） |
| `GatewaySubmitResult` | 新增 `RequestId`；`WebSocketGatewayClient.TryCompleteSubmitAckWaiter` 成功路径回填（服务端回填值兜底取帧 Id） |
| `ISessionClient` | 新增重载 `SubscribeAsync(sessionId, requestId, ct)`（请求/应答关联订阅契约） |
| `RemoteSessionClient` | `SubmitAsync` 透传 `RequestId`；关联订阅实现——**仅接受 Id 严格匹配的帧**（他轮帧与无关联帧——如 `GatewayDeliveryNotifier` 投递通知——一律跳过），仅匹配轮次的 `ExecutionComplete` 收口订阅；无参重载保持历史行为（UI/展示用途） |
| `InProcessSessionClient` | 关联重载忽略 `requestId`（进程内无共享队列串轮问题，等价无参重载） |
| QQ / WeCom 桥 | `_sessionClient.SubscribeAsync(sessionId, submit.RequestId, ct)` |

关联过滤天然覆盖：他轮滞留毒帧、跨会话帧串扰（同一 WS 连接多会话）、无关联广播帧（`BroadcastingGatewayEventSink` 经查仅定义未构造，死路径）。

### 6.3 回归测试

`RemoteSessionClientSequentialExchangeTests`（Client.Tests）：最小 Kestrel WS 伪服务端精确复刻 `HandleSubmitAsync` 帧编排（SubmitAck → 延迟 ChatEvent 轮次 → ExecutionComplete，全部带 frame.Id），真实 `WebSocketGatewayClient` + `RemoteSessionClient` 驱动**连续三轮**桥式交换（RunCompleted 提前 return）。修复前：第二轮返回空字符串（RED 复现线上缺陷）；修复后：三轮各收 `reply-1/2/3`（GREEN）。

## 7. ChannelHost 生命周期加固（宿主关闭遗留孤儿进程修复）

三层修复（缺一不可，覆盖全部关闭场景）：

1. **启动竞态**（`ChannelHostHostedService`）：自动启动链路（`ApplicationStarted` → 等待 Gateway 最多 30s → `StartEnabledAsync`）全链响应 `ApplicationStopping`；`Process.Start` 前最后检查；`ReconcileAsync` 在宿主停止后不再拉起新进程——消灭「宿主在等待窗口内关闭后 ChannelHost 仍被拉起」的竞态。
2. **停止链加固**（`ChannelHostManager`）：`StopAsync` 清理链（Kill / WaitForExit / 状态落盘）一律 `CancellationToken.None`（停止是尽力而为语义，不得与宿主 shutdown token 竞速半途而废）；`StopAllAsync` 状态加载移入逐项 try——正常关闭路径保证杀干净。
3. **父进程退出看门狗**（ChannelHost 侧，根本兜底）：`ChannelHostManager` 启动子进程附带 `--parent-pid`；ChannelHost 内 `ParentExitWatchdogService` 每 2s 轮询父进程存活，父进程死亡即优雅停止（走 `IHostApplicationLifetime.StopApplication`，桥/连接正常释放）——覆盖宿主被强杀（VS 停止调试 / 直接关控制台窗口 / 崩溃）等一切 `StopAsync` 不执行的场景（Windows 无进程树级联）。手动运行（未传 `--parent-pid`）不启用看门狗，不影响调试。

## 8. 部署验证建议

1. QQ 群交互消息：应送达真实回复，不再出现 `⚠️ user` / `(空回复)`。
2. **RC3 决定性验证**：启动 ChannelHost 后**连续发送至少三条**消息（`你是 → 好的 → 帮我查下明天的天气`）——三条都应收到真实回复；此前模式是「第一条正常、其后全部秒回 (空回复)」。
3. 每小时 cron：输出应为正常播报（事件流中可见真实 `current_time` 工具调用），不再群发原始工具调用文本。
4. 远程 WebUI（`:5020/sessions`）会话时间线核对 cron 轮的工具调用记录。
5. 主程序（WebUI/Gateway.Server）启动→ChannelHost 自动拉起；正常关闭（Ctrl+C）与强杀（直接关窗口/停止调试）后确认无遗留 `Seeing.Gateway.ChannelHost` 进程。
