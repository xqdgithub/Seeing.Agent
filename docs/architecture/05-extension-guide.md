# 05 扩展指南

## 1. 新增工具能力包（推荐路径）

1. 建目录 `src/capabilities/Seeing.Agent.Tools.Xxx/`（程序集名仍为 `Seeing.Agent.Tools.Xxx`）。  
2. **只**引用 Abstractions + Tools.Support（及 IO 接口）；可引用**原语层**（如 `Seeing.Session`，先例 `Seeing.Agent.TokenBudget` / `Seeing.Agent.Acp`）；**禁止**引用 Core。  
3. 实现工具：注入 `IFileSystem` / `ISubprocess` / `IExecutionWorld`，实现 `ITool`。  
4. 实现 `XxxModule : ISeeingModule`（+ 可选 `IUiContributor`，见 §5）：  
   - `Id = "xxx"`；声明 `ProvidedTools`  
   - `ConfigureServices`：`AddSingleton<XxxTool>()` + Options 节登记；**不要** `AddSingleton<ITool, XxxTool>`  
   - `ActivateAsync(sp, ct)`：`await tm.RegisterToolAsync(sp.GetRequiredService<XxxTool>(), ct)`  
   - `DeactivateAsync`：`UnregisterToolAsync`  
   - 需要 UI 时另实现 `IUiContributor.DescribeUi()`（纯数据；**不要**在 Activate/Deactivate 里注册/注销贡献），见 §5  
5. Sample `Program.cs`：`AddSeeingModule<XxxModule>(registry)`（在 `AddSeeingCore` **之前**）。  
6. 若需进命名启动集（`Boot=full` 等）：改 `BuiltInCapabilitySets`（boot SSOT）。内置工作模式 `full` 的模块是 `["*"]`，会自动包含已启动模块，不必把新 id 再写进场景。  
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

1. 实现 `ILlmClientFactory`：**只有一个成员** `Create(config, capability)`，按能力返回对应客户端（不支持返回 `null`，不抛）。协议不在工厂里声明。  
2. 包放 `src/capabilities/Seeing.Agent.Llm.Xxx/`。  
3. 模块 `ConfigureServices` 登记工厂，并登记「协议 → 工厂」映射：  
   ```csharp
   services.AddSingleton<XxxLlmClientFactory>();
   services.AddSingleton<ILlmClientFactory>(sp => sp.GetRequiredService<XxxLlmClientFactory>());
   services.AddSingleton<LlmClientFactoryRegistration>(sp => new(
       ProviderTypes.Xxx, sp.GetRequiredService<XxxLlmClientFactory>()));
   ```
   **必须按具体类型解析**：`ILlmClientFactory` 有多个实现，`GetRequiredService<ILlmClientFactory>()` 返回最后登记的那个，会把所有协议绑到同一个工厂。  
4. 旧 `providers.json` PascalCase 由 `ProviderTypes.Normalize` 兼容；写盘用小写。

### 品牌网关插件（DeepSeek / MiMo / OpenCodeZen / SiliconFlow 类）

1. 包可放 `plugs/providers/`；实现 `ILlmProvider` / `IConfigurableLlmProvider`（**禁止**引用 Core）。  
2. 若配置形状与 `ProviderConfig` 同构，可直接继承 `ConfigurableProviderBase<TOptions>`（`Seeing.Agent.Llm.Support`）：它提供能力解析、能力束生命周期、配置读写与释放；只需给出 `Protocol`、`DefaultCapabilities`、`BuildProviderConfig`、`CreateClient`、`LoadOptionsAsync` / `SaveOptionsAsync`。  
3. **能力由插件作者声明**（`DefaultCapabilities`），配置里 `capabilities` 可覆盖。不要按工厂推导——工厂实现了 embedding 不代表你的端点提供了 `/v1/embeddings`。装完即用，不要求用户先改配置。  
4. 实现 `ISeeingModule`（如 `provider.deepseek`），`DependsOn: ["llm.openai"]`（若走 OpenAI 兼容协议）。  
5. `ConfigureServices`：登记 Provider / Store；**不要** `AddHostedService` 旁路登记。  
6. `ActivateAsync`：先挂 `ILlmCallInterceptor`（若有），再 Warmup + `IProviderRegistry.Register`；`DeactivateAsync`：先 `Unregister` Provider，再卸拦截器。  
7. 创建客户端：注入 `ILlmClientFactoryResolver`（不是 `IEnumerable<ILlmClientFactory>`），用 `_factories.Require(Protocol).Create(config, capability)`。也可以完全跳过工厂、直接 `new` 自己的客户端——`ILlmProvider.GetClient` 是唯一契约。  
8. Sample：`AddSeeingModule<XxxLlmModule>(registry)`（在 `AddSeeingCore` 之前；模块类由 plug 提供，登记 API 在 Core）。

**LLM 调用边界扩展点（勿混层）：**

| 用途 | 载体 | 说明 |
|------|------|------|
| 静态连接头（UA 等） | `ProviderConfig.Headers` | 客户端创建时写入 DefaultRequestHeaders |
| 通用、多订阅、全 provider 头 | `llm.headers` Hook → `LlmCallContext.ExtraHeaders` | 编排层；流式/非流式均应用 |
| provider 专属出站塑形 | `ILlmCallInterceptor`（`AppliesTo`） | 如 OpenCode `x-opencode-session`；协议客户端**每次发送前** `Resolve`，经 `OutboundPipeline` 写入本次请求（非 Create 快照） |
| 横切（重试） | `ILlmClientDecorator` / `RetryLlmClientDecorator` | 镜像工具装饰器；工厂 Create 后包装 |
| 调用期身份 | `ILlmCapabilityClient.*(request, LlmCallContext? call, …)` | **不**塞进 `ChatRequest` |

**常规单次业务调用必须经 `ILlmInvoker`**（才是触发 `llm.*` Hook、合并出站头、计算耗时的唯一路径）。
只有连接测试探针、video 轮询、框架内部装配才直接 `ILlmRouter.Resolve<T>(modelId)`——判别口诀是
「这次调用该不该被观测成一次 LLM 调用」。见 `docs/architecture/28-llm-capability-abstraction-release-notes.md` §7.4.1。

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
- 内置能力集：`minimal` / `code` / `work` / `research` / `full` / **`secure`**（`*` − shell/mcp/acp/gateway/files.workspace）/ **`dev`**（= full）。  
- 快速切换启动档：`seeing web --boot secure`，或环境变量 `SEEING_BOOT=secure`，或 `dotnet run --project samples/Seeing.Agent.WebUI -- --boot secure`（覆盖文件 `Boot`）。  
- 同名覆盖内置能力集 / 场景；删除覆盖即回退内置。未在宿主引用的模块 id 不会真正启用。  
- 若只需缩工具、不卸模块：写场景或全局的 `Tools.Disabled`，不要把工具 id 写进模块层 `Disabled`。  
- 迁移：旧「`Scenario=minimal` 当沙箱」→ `Boot=minimal`；旧 `Modules.Enabled` → 本例式 CapabilitySet + `Boot`；扁平 `ToolsDisabled` → `Tools.Disabled`。

## 5. WebUI 扩展

机制全貌见 [`40 UI 扩展统一架构`](40-ui-extension-unified-architecture-release-notes.md)；规范条款见 [`04 §11 E1–E7`](04-development-standards.md)。

三层分工：**契约**（`Abstractions/Ui/`，零实现）→ **脊柱**（`Core/Ui/`，注册表 / 可见性 / 路由匹配）→ **宿主**（`WebUI/Shell/`，扩展点声明 + 视图表）。

| 想扩展什么 | 载荷 | 挂在哪个点 |
|---|---|---|
| 侧栏入口 / 页面 | `PageContribution`（`Nav != null`） | `shell.page` |
| 只有 URL 可达的页面（详情页 / 创建页） | `PageContribution`（`Nav = null`） | `shell.page` |
| 设置页页签 | `SettingsCardContribution` | `shell.settings.card` |
| 会话窗口插槽 | `SlotContribution` | `session.prompt` / `session.toolbar.actions` / `session.input.toolbar` / `session.file.panel` |

**禁止**：给模块页加 `@page`（`Pages/_Host.cshtml` 是唯一保留者）；在 `Activate/Deactivate` 里注册/注销贡献；在契约层放 `Type` / `RenderFragment`。

### 5.1 新增页面

1. **写组件**（`samples/Seeing.Agent.WebUI/Pages/` 或 `Components/`），**不加 `@page`**。路由参数用 `[Parameter]`，且**参数名逐字等于**模板占位名：

   ```razor
   @* Route = "/xxx/{ItemId}" ⇒ 参数名必须是 ItemId *@
   [Parameter] public string ItemId { get; set; } = "";
   ```

2. **模块声明**（能力包侧，只引用 Abstractions）：

   ```csharp
   public sealed class XxxModule : ISeeingModule, IUiContributor
   {
       public IReadOnlyList<UiContribution> DescribeUi() =>
       [
           new PageContribution
           {
               Point = "shell.page",
               Id = "page.xxx",
               Route = "/xxx",
               Title = "某某",
               ViewKey = "page.xxx",
               Requires = [Id],        // 归属自身模块
               Order = 70,
               Nav = new NavPlacement
               {
                   Icon = "star",                  // 中性 token，宿主经 WebUiIconMap 翻译
                   Group = NavGroups.Workspace,    // 控制中心 / 工作区 / 设置
                   GroupIcon = NavGroups.WorkspaceIcon,
                   GroupOrder = 20,
               },
           },
       ];
   }
   ```

   详情页 / 创建页**不写 `Nav`**（`null` = 纯路由）。不写 `Nav` 还有一个效果：`SidebarProjectionService` 的前缀规则不会再让它出现在侧栏。

3. **宿主登记视图**（`samples/Seeing.Agent.WebUI/Shell/WebUiViews.cs`）：

   ```csharp
   resolver.Register("page.xxx", typeof(XxxPage));
   ```

   漏这一步会在启动日志里出现 `ValidatorRejected`，页面直链会落到「未启用」占位。

4. **跑守门测试**：`PageChannelMigrationTests.MigratedRouteSet_ShouldCoverAllLegacyRoutes` 的期望表要同步加路由；`EveryPageViewKey_ShouldBeResolvable` 会自动覆盖新 `ViewKey`。

**想挂壳层页面**（不属于任何模块、切 Boot 后仍须可达）：不要做成模块贡献，改在 `Shell/WebUiHostContributor.cs` 加一条 `Requires = []` 的 `PageContribution`，并在 `WebUiViews` 登记。

### 5.2 新增设置卡

1. 写卡片组件（`Components/Settings/`），不要加 `@page`、不要自带路由。
2. 声明：

   ```csharp
   new SettingsCardContribution
   {
       Point = "shell.settings.card",
       Id = "settings.xxx",
       Title = "某某",
       ViewKey = "settings.xxx",
       Requires = ["xxx"],     // 指向拥有该卡片的模块
       Order = 50,             // 决定页签顺序（注册表全局按 OwnerId → 扩展点 → Order → Id 排序）
   }
   ```

   `Requires` **不要留空**：进程级设置卡会在模块停用后仍显示，点进去全是失效控件。

3. 在 `WebUiViews.RegisterBuiltIns` 登记 `"settings.xxx"`。
4. 页签 key 由 `Settings.razor` 用 `{OwnerId}.{Id}` 拼，无需自己处理唯一性。

> 由**宿主代模块声明**的卡片（如 `settings.scheduler` / `settings.memory`）写在 `WebUiHostContributor.Card(...)` 里，`OwnerId` 仍是 `host.webui`，归属写进 `Requires`。适用条件：组件住在 WebUI、但业务归属是某个能力模块。

### 5.3 新增插槽

插槽 = 宿主预先声明的「会话窗口上的一个位置」。**新增一个插槽点需要改宿主**（`Shell/WebUiPoints.cs` 加 `UiPoint`，`All` 里登记，并在挂载组件处放 `<SlotHost SlotName="..."/>` + 槽名到点 id 的映射）。若已有合适的点，模块侧只需：

```csharp
new SlotContribution
{
    Point = "session.prompt",
    Id = "slot.xxx",
    ViewKey = "slot.xxx",
    Requires = [Id],
    DisplayName = "某某",
}
```

要点：
- 点的 `Scope` 是 `Session` 时，可见性 = 模块启用 **AND** 会话工作模式门集（`UiScenarioGate`）。`null` 门集不收敛，空集全隐藏。
- `SlotHost` 已把每条贡献包在 `ErrorBoundary` 里；自己写挂载点时也要包（渲染期未捕获异常会终止整个 circuit）。
- 四点映射（旧槽名 → 扩展点 id）见 `SlotHost.razor` 的 `s_slotPoints` 表；未登记的槽名渲染空容器，不抛。

### 5.4 别做成贡献的东西

侧栏「聊天」「审批中心」、侧栏底部工具条、首页 dashboard 卡片、斜杠命令、通知横幅、Token 预算状态栏、会议页侧栏分区——理由逐条记在 [`40 §5.1`](40-ui-extension-unified-architecture-release-notes.md)。想加这类入口时先读那张表，别把信封撑成万能基类。

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

- Shape：`Seeing.Agent.Hosting.Embed`（库契约；仓库内**不再提供示例宿主**）  
- 默认 `HostDefaultBoot = "minimal"`；嵌入方可用 `seeing.json` 的 `Boot` 或 `--boot` 切换  
- 能力集仍由嵌入方自己的 `AddSeeingModule*` 决定；可参考 `samples/Seeing.Agent.HostComposition` 的 Full 组合方式  

## 10. 最小可运行 Sample 清单

至少：

- `LocalExecutionWorldModule`（io.local）  
- 一个 `llm.*`  
- `AgentsBuiltInModule`（若需内置人格）  
- 所需 Tools 模块  
- `AddSeeingCore` + `InitializeSeeingAsync`  

否则 `bootEnabled` / 结算 base 可能为空或硬依赖失败。仓库内 4 个宿主统一经
`samples/Seeing.Agent.HostComposition` 的 `AddSeeingFullCapabilities` 注册 Full 能力集；
新增能力模块只改该共享组合根。

## 11. 工具进度与元数据通道

- **流式/进度**：经 `ToolContext.EventSink` 发 `ToolCallEvent`（`Status=Running`，`Output`/`Title`）。  
- **终态结构化字段**（如 bash 的 `exit`/`timedOut`）：写入 `ToolResult.Metadata`，由 `AgentExecutor` 打进 Complete 事件 → Session `Metadata`。  
- **`IToolMetadataSink`**：当前 **未接线**（`ToolManager` 传入 `setMetadata: null`），`SetMetadata` 为 no-op，**请勿依赖**。  
- **耗时**：使用事件一等字段 `Duration` / Session `duration_ms`；不要塞进 Metadata。  
- **工具卡片识别**：按工具名（及 Task 的显式 `TaskId`）；禁止用 Output 文本启发式。展示特化用投影模型（如 `BashCardModel`），勿向 `ToolCallViewModel` 堆专用属性。

## 12. 扩展完成自检

- [ ] 磁盘路径在正确分类目录；slnx Folder 已加  
- [ ] csproj 无 Core（能力包）/ 无违规互引  
- [ ] 新增能力模块已加入 `BuiltInCapabilitySets.Full` **且** `samples/Seeing.Agent.HostComposition`
      （4 个宿主共用同一注册点；`Seeing.Agent.HostComposition.Tests` 会守门）  
- [ ] Activate / Deactivate 成对；未启用时无工具、无 UI、无连接、无后台循环  
- [ ] Options 已 Register；改 seeing.json 可热反应或有文档说明「需重启」  
- [ ] 未在 executor 双算 schema  
- [ ] 工具进度走 EventSink；终态私有字段走 ToolResult.Metadata  
- [ ] 构建 + Invariants 通过  
- [ ] README 扫描命令无意外命中  
