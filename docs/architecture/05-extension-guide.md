# 05 扩展指南

## 1. 新增工具能力包（推荐路径）

1. 建目录 `src/capabilities/Seeing.Agent.Tools.Xxx/`（程序集名仍为 `Seeing.Agent.Tools.Xxx`）。  
2. **只**引用 Abstractions + Tools.Support（及 IO 接口）；可引用**原语层**（如 `Seeing.Session`，先例 `Seeing.Agent.TokenBudget` / `Seeing.Agent.Acp`）；**禁止**引用 Core。  
3. 实现工具：注入 `IFileSystem` / `ISubprocess` / `IExecutionWorld`，实现 `ITool`。  
4. 实现 `XxxModule : ISeeingModule`（+ 可选 `IUiContribution`）：  
   - `Id = "xxx"`；声明 `ProvidedTools`  
   - `ConfigureServices`：`AddSingleton<XxxTool>()` + Options 节登记；**不要** `AddSingleton<ITool, XxxTool>`  
   - `ActivateAsync(sp, ct)`：`await tm.RegisterToolAsync(sp.GetRequiredService<XxxTool>(), ct)` + UI Register  
   - `DeactivateAsync`：`UnregisterToolAsync` + UI Unregister  
5. Sample `Program.cs`：`AddSeeingModule<XxxModule>(registry)`（在 `AddSeeingCore` **之前**）。  
6. 若需进内置启动集 / 工作模式：改 `BuiltInCapabilitySets`（boot SSOT）及引用它的 `BuiltInScenarios.Modules`（编译期常量，勿复制数组、勿引用工具类型）。  
7. 测试放 `tests/capabilities/Seeing.Agent.Tools.Xxx.Tests/`；补不变量（未启用则 schema 无该工具）。  
8. 把项目加入 `Seeing.Agent.slnx` 的 `/src/capabilities/` 与 `/tests/capabilities/` Folder。

## 2. 新增非工具模块（Memory/Scheduler 类）

同上目录约定（`src/capabilities/`），并额外：

- HostedService 实现 `IModuleHostedService`，经 `AddModuleHostedService<T>` 登记  
- 连接：Owner 空壳 + Activate Open / Deactivate Close  
- 配置节自注册；Core 不认识你的 Options 形状  
- 避免能力包互硬引用 Skills/Mcp；需要协作时经 Abstractions 端口

## 3. 新增 LLM Provider

### 协议工厂（OpenAI / Anthropic 类）

1. 实现 `ILlmClientFactory`，`SupportedTypes` 声明类型字符串（小写，如 `openai`）。  
2. 包放 `src/capabilities/Seeing.Agent.Llm.Xxx/`。  
3. 模块 `ConfigureServices` 登记工厂；由 Core `ProviderManager` 聚合。  
4. 旧 `providers.json` PascalCase 由 `ProviderTypes.Normalize` 兼容；写盘用小写。

### 品牌网关插件（DeepSeek / OpenCodeZen 类）

1. 包可放 `plugs/providers/`；实现 `ILlmProvider` / `IConfigurableLlmProvider`（**禁止**引用 Core；工厂解析用 `LlmClientFactoryResolver`）。  
2. 实现 `ISeeingModule`（如 `provider.deepseek`），`DependsOn: ["llm.openai"]`（若走 OpenAI 兼容协议）。  
3. `ConfigureServices`：登记 Provider / Store；**不要** `AddHostedService` 旁路登记。  
4. `ActivateAsync`：先挂 `ILlmCallInterceptor`（若有），再 Warmup + `IProviderRegistry.Register`；`DeactivateAsync`：先 `Unregister` Provider，再卸拦截器。  
5. 创建客户端：注入 `IEnumerable<ILlmClientFactory>`，用 `LlmClientFactoryResolver.Require(..., ProviderTypes.OpenAi)`，**禁止**注入单个 `ILlmClientFactory`。  
6. Sample：`AddSeeingModule<XxxLlmModule>(registry)`（在 `AddSeeingCore` 之前；模块类由 plug 提供，登记 API 在 Core）。

**LLM 调用边界扩展点（勿混层）：**

| 用途 | 载体 | 说明 |
|------|------|------|
| 静态连接头（UA 等） | `ProviderConfig.Headers` | 客户端创建时写入 DefaultRequestHeaders |
| 通用、多订阅、全 provider 头 | `chat.headers` Hook → `LlmCallContext.ExtraHeaders` | 编排层；流式/非流式均应用 |
| provider 专属出站塑形 | `ILlmCallInterceptor`（`AppliesTo`） | 如 OpenCode `x-opencode-session`；协议客户端**每次发送前** `Resolve`，经 `OutboundPipeline` 写入本次请求（非 Create 快照） |
| 横切（重试） | `ILlmClientDecorator` / `RetryLlmClientDecorator` | 镜像工具装饰器；工厂 Create 后包装 |
| 调用期身份 | `ILlmClient.Complete*(request, LlmCallContext? call, …)` | **不**塞进 `ChatRequest` |

公共扩展点用**类型化接口**（非 Func）。详见 `docs/superpowers/specs/2026-09-09-llm-provider-abstraction-redesign.md`。

## 4. 自定义 Boot / CapabilitySet 与 Scenario

**UI：** 设置 → **启动能力**（`Boot` + `CapabilitySets`）；设置 → **场景**（工作模式 CRUD / 另存为）。  

**文件（PascalCase；模块层 `Disabled`；工具层 `Tools.Disabled`）：**

```json
{
  "SeeingAgent": {
    "Boot": "my-lab-boot",
    "CapabilitySets": {
      "my-lab-boot": {
        "Modules": ["io.local", "agents.builtin", "llm.openai", "basic", "filesystem"],
        "Disabled": []
      }
    },
    "Scenario": "my-lab",
    "Scenarios": {
      "my-lab": {
        "Modules": ["io.local", "agents.builtin", "llm.openai", "basic", "filesystem"],
        "DefaultAgent": "build",
        "Seams": { "executionWorld": "io.local" },
        "Tools": { "Disabled": [] }
      }
    },
    "Modules": {
      "Disabled": [],
      "Tools": { "Disabled": [] }
    },
    "Seams": { "executionWorld": "io.local" }
  }
}
```

- `Boot` 决定进程 `bootEnabled` / Activate；`Scenario` 只是默认工作模式（会话可再切换，且只能 ∩ `bootEnabled`）。  
- CapabilitySet / Scenario **各自**持有 `Modules`，**无**外键；运行时求交。  
- 内置能力集：`minimal` / `code` / `work` / `research` / `full` / **`secure`**（`*` − shell/mcp/acp/gateway）/ **`dev`**（= full）。  
- 快速切换启动档：`seeing web --boot secure`，或环境变量 `SEEING_BOOT=secure`，或 `dotnet run --project samples/Seeing.Agent.WebUI -- --boot secure`（覆盖文件 `Boot`）。  
- 同名覆盖内置能力集 / 场景；删除覆盖即回退内置。未在宿主引用的模块 id 不会真正启用。  
- 若只需缩工具、不卸模块：写场景或全局的 `Tools.Disabled`，不要把工具 id 写进模块层 `Disabled`。  
- 迁移：旧「`Scenario=minimal` 当沙箱」→ `Boot=minimal`；旧 `Modules.Enabled` → 本例式 CapabilitySet + `Boot`；扁平 `ToolsDisabled` → `Tools.Disabled`。

## 5. WebUI 扩展

| 扩展点 | 类型 |
|--------|------|
| 侧栏 | `NavContribution`（`Group`/`Order`/`ComponentType`） |
| 路由（可无侧栏） | `RouteContribution` |
| 设置页签 | `SettingsCardContribution` |
| 会话插槽 | `SlotContribution` |
| 消息渲染 | `MessageRendererContribution` |

Activate：`IUiContributionRegistry.Register`；Deactivate：`Unregister(moduleId)`。  
**禁止**给模块页加 `@page`；壳页也不要加（仅 `_Host.cshtml`）。

## 6. 执行器扩展（ACP 等）

- 实现 `IAgentExecutorImplementation` 并登记  
- **禁止** `Replace` 掉 `IAgentExecutor` 门面；由 Router 按 Runtime 分派  
- 包放 `src/capabilities/`；只依赖 Abstractions  

## 7. 命令扩展

- 实现 `ICommand`，在模块 Activate 时写入 `ICommandRegistry`（或 DI `GetServices<ICommand>` 扫描）  
- Hosting 不硬引用能力包命令类型  

## 8. Gateway 组合

| 层 | 包 | 谁引用 |
|----|-----|--------|
| Shape | `Seeing.Agent.Hosting.Gateway` | sample：`AddSeeingHostingGateway()` |
| 集成 | `Seeing.Agent.Gateway` | **同一 sample**：`AddSeeingGatewayServer(...)` |
| 协议/通道 | `Seeing.Gateway*` | 集成包 / ChannelHost |

**禁止**让 Hosting.Gateway Shape 项目引用 Agent.Gateway。详见 [`docs/gateway/README.md`](../gateway/README.md)。

## 9. Embed 宿主

- Shape：`Seeing.Agent.Hosting.Embed`  
- Sample：`samples/Seeing.Agent.Embed.Demo`  
- 能力集仍由该 sample 的 `AddSeeingModule*` 决定，与 Web 相同规则  

## 10. 最小可运行 Sample 清单

至少：

- `LocalExecutionWorldModule`（io.local）  
- 一个 `llm.*`  
- `AgentsBuiltInModule`（若需内置人格）  
- 所需 Tools 模块  
- `AddSeeingCore` + `InitializeSeeingAsync`  

否则 `bootEnabled` / 结算 base 可能为空或硬依赖失败。Embed 等 Shape 可通过 `HostDefaultBoot`（如 `minimal`）收窄默认启动集。

## 11. 工具进度与元数据通道

- **流式/进度**：经 `ToolContext.EventSink` 发 `ToolCallEvent`（`Status=Running`，`Output`/`Title`）。  
- **终态结构化字段**（如 bash 的 `exit`/`timedOut`）：写入 `ToolResult.Metadata`，由 `AgentExecutor` 打进 Complete 事件 → Session `Metadata`。  
- **`IToolMetadataSink`**：当前 **未接线**（`ToolManager` 传入 `setMetadata: null`），`SetMetadata` 为 no-op，**请勿依赖**。  
- **耗时**：使用事件一等字段 `Duration` / Session `duration_ms`；不要塞进 Metadata。  
- **工具卡片识别**：按工具名（及 Task 的显式 `TaskId`）；禁止用 Output 文本启发式。展示特化用投影模型（如 `BashCardModel`），勿向 `ToolCallViewModel` 堆专用属性。

## 12. 扩展完成自检

- [ ] 磁盘路径在正确分类目录；slnx Folder 已加  
- [ ] csproj 无 Core（能力包）/ 无违规互引  
- [ ] Activate / Deactivate 成对；未启用时无工具、无 UI、无连接、无后台循环  
- [ ] Options 已 Register；改 seeing.json 可热反应或有文档说明「需重启」  
- [ ] 未在 executor 双算 schema  
- [ ] 工具进度走 EventSink；终态私有字段走 ToolResult.Metadata  
- [ ] 构建 + Invariants 通过  
- [ ] README 扫描命令无意外命中  
