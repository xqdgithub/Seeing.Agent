## 1. 基线度量

- [ ] 1.1 在 `src/hosting/Seeing.Agent.Hosting/Execution/ExecutionRunner.cs` 的 `BuildExecutionContextAsync` 末尾新增 Debug 日志，输出 `inlineToolCount` / `deferredToolCount` / `schemaTokens` / `catalogChars` / `exposureMode`
- [ ] 1.2 用 WebUI `full` 场景记录改造前基线（工具数、首轮 input tokens、首轮耗时），写入 `openspec/changes/tool-context-budgeting/baseline.md`
- [ ] 1.3 新增测试辅助 `tests/spine/Seeing.Agent.Tests/Tools/ToolManagerTestHarness.cs`：可注入多个假 `ITool`、可注入计数型 `IHookManager`（记录 `tool.definition` 触发次数）
- [ ] 1.4 构建并跑既有测试确认基线为绿：`dotnet build Seeing.Agent.slnx` → `dotnet vstest tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll`

## 2. Hook 版本（tool-schema-settlement）

- [ ] 2.1 写失败测试 `HookManagerTests.Version_ShouldIncreaseOnRegister`：注册 Hook 前后版本递增
- [ ] 2.2 写失败测试 `HookManagerTests.Version_ShouldIncreaseOnRemoveAndClear`
- [ ] 2.3 在 `src/abstractions/Seeing.Agent.Abstractions/Hooks/IHookManager.cs` 新增 `long Version { get; }`
- [ ] 2.4 在 `src/spine/Seeing.Agent.Core/Core/Hooks/HookManager.cs` 实现该属性：`private long _version;` + `Interlocked.Increment`，在 `Register`/`RegisterMulti`/`Remove`/`Clear` 成功后递增
- [ ] 2.5 运行 2.1–2.2 与 `tests/spine/Seeing.Agent.Tests/Hooks/` 全量

## 3. ToolManager：先交集后生成 + 稳定排序（tool-schema-settlement）

- [ ] 3.1 写失败测试 `ToolManagerSchemaSettlementTests.GetToolSchemasAsync_WithSubsetSettled_ShouldOnlyGenerateSettledTools`：注册 5 个工具，settled 传 2 个，断言返回 2 条且 `tool.definition` Hook 触发 2 次
- [ ] 3.2 运行该测试确认失败
- [ ] 3.3 在 `src/spine/Seeing.Agent.Core/Tools/ToolManager.cs` 提取 `private async Task<FunctionToolSchema?> BuildSchemaAsync(ITool tool, CancellationToken ct)`，含现有 `tool.definition` Hook 逻辑（从 `:290-325` 平移）
- [ ] 3.4 改写 `GetToolSchemasAsync(settledToolIds, agent, ct)`（`:88-103`）：先求 `settled ∩ enabled`，再应用 `DeniedTools`，若有 `AllowedTools` 再求交；仅对存活 id 调 `BuildSchemaAsync`
- [ ] 3.5 保留无参 `GetToolSchemasAsync()` 作为诊断用，内部改为遍历 `_tools.Keys` 调 `BuildSchemaAsync`
- [ ] 3.6 运行 3.1 确认通过
- [ ] 3.7 写失败测试 `GetToolSchemasAsync_ShouldReturnStableOrder`：注册顺序 `z_tool, a_tool, m_tool`，连续两次调用，断言两次 id 序列相同且为 `a_tool, m_tool, z_tool`
- [ ] 3.8 在返回前按 `Function.Name` 做 `OrderBy(StringComparer.OrdinalIgnoreCase)`；运行 3.7 确认通过
- [ ] 3.9 运行 `tests/spine/Seeing.Agent.Tests` 全量，确认 `AgentModeFilterTests` / `ToolInvokerTests` / `SessionToolSchemaResolverTests` 仍绿

## 4. ToolManager：schema 缓存（tool-schema-settlement）

- [ ] 4.1 写失败测试 `GetToolSchemasAsync_WithSameKey_ShouldHitCache`：连续两次同参调用，Hook 触发次数不增加
- [ ] 4.2 写失败测试 `GetToolSchemasAsync_AfterToolDisabled_ShouldInvalidateCache`
- [ ] 4.3 写失败测试 `GetToolSchemasAsync_AfterDefinitionHookChanged_ShouldInvalidateCache`：先无 Hook 调用，再注册改写 description 的 Hook 后调用，断言第二次为改写值
- [ ] 4.4 在 `ToolManager` 新增 `ConcurrentDictionary<SchemaCacheKey, IReadOnlyList<FunctionToolSchema>>`，容量上限 32（超限按最旧淘汰）与 `_toolStateVersion`（禁用集每次变更 `Interlocked.Increment`）
- [ ] 4.5 `SchemaCacheKey` 含：排序后的目标 id 集、Agent `AllowedTools`/`DeniedTools`、`_toolStateVersion`、`_hookManager.Version`
- [ ] 4.6 新增能力键 `ToolCapabilityKeys.Cacheable`（`"cacheable"`，默认 true）与读取逻辑：为 false 的工具不进入缓存
- [ ] 4.7 运行 4.1–4.3 与 `tests/spine/Seeing.Agent.Tests/Tools` 全量

## 5. 工具定义 token 计量（tool-schema-settlement）

- [ ] 5.1 在 `src/hosting/Seeing.Agent.Hosting/Seeing.Agent.Hosting.csproj` 增加 `<ProjectReference Include="..\..\primitives\Seeing.TokenEstimation\Seeing.TokenEstimation.csproj" />`（**禁止**引用 `Seeing.Agent.TokenBudget`）
- [ ] 5.2 在 `src/primitives/Seeing.Session/Core/SessionData.cs` 新增 `CachedSchemaTokens`（`int?`）与 `CachedCatalogTokens`（`int?`），紧邻既有 `CachedInputTokens` 的「Token Usage 缓存」段
- [ ] 5.3 让新字段与 `CachedInputTokens` 同进同出：`ClearMessages()` 与 `ClearUsageCache()` 置 null；fork/clone 逐字段复制处（`SessionData.cs:484-486`）新增两字段赋值
- [ ] 5.4 在 `src/primitives/Seeing.TokenEstimation/SourceBreakdown.cs` 新增 `Catalog` 字段，并在 `Total` 与 `ToDictionary()`（键 `"catalog"`）中计入
- [ ] 5.5 写失败测试 `ToolTokenAccountingTests.ShouldEstimateSchemaTokens`：3 个 schema 求和 → `SourceBreakdown.ToolDefinitions > 0`；`Catalog` 单列、不计入 `ToolDefinitions`
- [ ] 5.6 在 `TokenBudgetManager.CalculateBreakdown` 移除 `toolTokens` 形参（`ITokenBudgetManager` 声明 + 实现 + 全部调用点，均已验证不传该参），改为内部读 `session.CachedSchemaTokens`→`ToolDefinitions`、`session.CachedCatalogTokens`→`Catalog`
- [ ] 5.7 在 `BuildExecutionContextAsync` 用 `ITokenCounter`（未注册回退 `new CharBasedTokenCounter()`）对 `schemaSettlement.ToolSchemas` 逐条 `Description + Parameters.ToString()` 求和，写入 `session.CachedSchemaTokens`（`CachedCatalogTokens` 本期置 0，目录 token 于组 12 接入）
- [ ] 5.8 写测试 `CalculateBreakdown_ReadsSessionCachedTokens_ShouldPopulateToolDefinitions`：session 设 `CachedSchemaTokens=123`/`CachedCatalogTokens=45` → breakdown `ToolDefinitions==123`/`Catalog==45`/`Total` 含二者
- [ ] 5.9 写测试 `SessionDataTests.ClearUsageCache_ShouldNullSchemaTokenCache` 与 `SessionForkerTests.Fork_ShouldCopySchemaTokenCache`
- [ ] 5.10 运行 5.5/5.8/5.9 与 `tests/primitives/Seeing.TokenEstimation.Tests`、`tests/spine/Seeing.Agent.Tests/TokenBudget/`、`tests/primitives/Seeing.Session.Tests`
- [ ] 5.11 确认 `ChatExecutionContext` **不**新增 token 字段（估算走 `SessionData` 归属，非跨包通道）；删除旧「Metadata 携带 / toolTokens 传参」的接线残留（若有）

## 6. ExecutionRunner 依赖接口化（tool-schema-settlement）

- [ ] 6.1 写失败测试 `ExecutionRunner_WithCustomResolver_ShouldUseIt`：注册自定义 `ISessionToolSchemaResolver` 返回固定 1 条 schema，断言执行上下文 `ToolSchemas` 为该 1 条
- [ ] 6.2 修改 `ExecutionRunner.cs:61,72,82-83`：字段与构造参数改为 `ISessionToolSchemaResolver`，类型改为必需注入
- [ ] 6.3 删除 `schemaResolver ?? new SessionToolSchemaResolver(...)` 回退分支；未注册时在构造期抛出明确异常
- [ ] 6.4 写失败测试 `ExecutionRunner_WithoutResolver_ShouldFailFast`：容器无注册时构造抛异常
- [ ] 6.5 更新 `docs/architecture/18-gateway-live-subscription-and-schema-resolver-release-notes.md` §3.2 中"null 时回退构造同一实现"的表述
- [ ] 6.6 运行 6.1、6.4 与 `tests/spine/Seeing.Agent.Tests/App/Execution/`、`tests/capabilities/Seeing.Agent.Scheduler.Tests/AgentJobToolSchemasTests.cs`

## 7. MCP 工具命名归一（mcp-tool-naming，零兼容）

- [ ] 7.1 写失败测试 `McpTool_Id_ShouldUseDoubleUnderscoreForm`：`("docs","search")` → `mcp__docs__search`
- [ ] 7.2 写失败测试 `McpTool_Ids_ShouldNotCollideForUnderscoreAmbiguity`：`("a_b","c")` 与 `("a","b_c")` 的 id 不相等
- [ ] 7.3 修改 `src/capabilities/Seeing.Agent.Mcp/McpTool.cs:18`：`Id => $"mcp__{_serverName}__{_realName}"`；新增 `public string Namespace => $"mcp__{_serverName}"`
- [ ] 7.4 修改 `src/capabilities/Seeing.Agent.Mcp/Management/McpConnectionCoordinator.cs` 中全部 id 拼接处（约 `:164`、`:329`、`:570`）：统一为 `mcp__{server}__{tool}`
- [ ] 7.5 写失败测试 `McpConnectionCoordinatorTests.RegistryKeys_ShouldUseNewForm`：连接后发现工具，断言注册表键为新形式
- [ ] 7.6 修改 `src/capabilities/Seeing.Agent.Mcp/Management/McpToolRegistry.cs`：重复 server 名注册时**失败并抛明确异常**（当前 `_mcpTools[toolId] = mcpTool` 静默覆盖）
- [ ] 7.7 写失败测试 `McpToolRegistryTests.DuplicateServerName_ShouldThrow`
- [ ] 7.8 修改 `src/hosting/Seeing.Agent.Hosting/Commands/ToolsCommands.cs:39`：`StartsWith("mcp_")` → `StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)`
- [ ] 7.9 逐一核对四个消费点只认新 id（无兼容分支）：`ToolManager.IsToolEnabled`、`FilterSchemasByAgentToolLists`、权限规则 `PermissionKind.Tool` 的 resource 匹配（`src/spine/Seeing.Agent.Core/Core/Permission/`）、`/mcp` 列表判定
- [ ] 7.10 写失败测试 `PermissionRule_McpTool_ShouldMatchNewId`：规则声明 `mcp__docs__search` 为 Ask 时调用进入审批
- [ ] 7.11 运行 `tests/capabilities/Seeing.Agent.Mcp.Tests`、`tests/spine/Seeing.Agent.Tests/MCP/`、`tests/spine/Seeing.Agent.Tests/Permissions/` 全量；旧 id 断言直接改为新 id，**不得**保留旧 id 测试

## 8. 暴露策略契约（tool-exposure-policy）

- [ ] 8.1 在 `src/abstractions/Seeing.Agent.Abstractions/Tools/` 新增 `ToolExposureMode.cs`：`enum ToolExposureMode { Inline, Deferred, Hidden }`
- [ ] 8.2 新增 `ToolExposurePolicyMode.cs`：`enum ToolExposurePolicyMode { All, Auto, Tiered }`（**与 8.1 分开，不共用类型**）
- [ ] 8.3 新增 `ToolCatalogEntry.cs`：`record ToolCatalogEntry(string Id, string? Namespace, string Signature, string Summary, int EstimatedTokens, bool Pinned, ToolExposureMode Mode)`（`Summary` 已截断到 `CatalogLineMaxChars`）
- [ ] 8.4 新增 `ToolExposurePlan.cs`：`record ToolExposurePlan(IReadOnlyList<FunctionToolSchema> InlineSchemas, IReadOnlyList<ToolCatalogEntry> CatalogEntries, int InlineToolCount, int DeferredToolCount, int EstimatedDefinitionTokens)`
- [ ] 8.5 新增 `ToolExposureInput.cs`：`record ToolExposureInput(ToolExposurePolicyMode Mode, IReadOnlyList<ToolCatalogEntry> Available, IReadOnlySet<string> DiscoveredIds, IReadOnlyList<string> PinnedIds, int CatalogBudgetChars, int CatalogLineMaxChars, bool Escalated)`
- [ ] 8.6 新增 `IToolExposurePolicy.cs`：`ToolExposurePlan Plan(ToolExposureInput input)`（**纯同步、单方法、无 CancellationToken、无 DI**）
- [ ] 8.7 新增 `IToolDiscoveryStore.cs`：`IReadOnlySet<string> Get(string sessionId)` / `void Add(string sessionId, IEnumerable<string> toolIds)` / `void Prune(string sessionId, IReadOnlySet<string> availableToolIds)`
- [ ] 8.8 扩展 `SessionToolSchemaResolution`（`ISessionToolSchemaResolver.cs`）：新增 `IReadOnlyList<ToolCatalogEntry> CatalogEntries`、`int InlineToolCount`、`int DeferredToolCount`、`int EstimatedDefinitionTokens`、`int CatalogTokens`；保留原两参构造重载
- [ ] 8.9 编译确认契约层零实现泄漏：`dotnet build src/abstractions/Seeing.Agent.Abstractions`

## 9. 策略实现（纯函数）（tool-exposure-policy）

- [ ] 9.1 新增 `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/AllToolExposurePolicy.cs`：全部 `Inline`（含元工具 → `Hidden`，见 9.5）
- [ ] 9.2 新增 `TieredToolExposurePolicy.cs`：pinned → `Inline`；`discovered ∩ available` → `Inline`；其余 → `Deferred`；`tool_search` → `Inline`
- [ ] 9.3 新增 `ToolCatalogBuilder.cs`（纯静态）：按命名空间分组、生成单行签名、描述截断到 `CatalogLineMaxChars`、按 `(namespace, id)` 确定性排序
- [ ] 9.4 实现目录预算算法：先为每个命名空间保留一行，再按"签名行长度升序"轮转补全，直到 `CatalogBudgetChars`（默认 2000 字符）耗尽；未完整展示的命名空间标注 `(N tools, M shown)`
- [ ] 9.5 新增 `ToolExposurePolicySelector.cs`：按 `ToolExposurePolicyMode` 分派到对应策略；`All` 模式下把 `tool_search` 标为 `Hidden`
- [ ] 9.6 新增 `ExposureEscalationTracker.cs`：`bool ShouldEscalate(int toolCount, int schemaTokens, bool alreadyEscalated)`，读 `Tools.Exposure.EscalateToolCount` / `EscalateSchemaTokens`；已升级直接返回 true（单调）
- [ ] 9.7 写测试 `TieredToolExposurePolicyTests`：pinned 保持 inline、discovered 保持 inline、消失的 discovered 被丢弃、`tool_search` 恒 inline
- [ ] 9.8 写测试 `AllToolExposurePolicyTests.DiscoveryTool_ShouldBeHidden`
- [ ] 9.9 写测试 `ToolCatalogBuilderTests.ShouldRetainEveryNamespaceUnderBudget`：5 命名空间 60 工具、预算仅容 12 行，断言每命名空间至少 1 行
- [ ] 9.10 写测试 `ToolCatalogBuilderTests.ShouldTruncateDescriptionAtLimit`
- [ ] 9.11 写测试 `ExposureEscalationTrackerTests`：`ShouldEscalateByToolCount`（30 个 / 3000 token）、`ShouldEscalateByTokens`（5 个 / 8000 token）、`ShouldStayAllBelowBoth`（11 个 / 4400 token）、`ShouldStayEscalatedOnceRaised`
- [ ] 9.12 写测试 `TieredToolExposurePolicyTests.ShouldBePure`：相同输入两次调用产出相同计划

## 10. 配置与作用域（tool-exposure-policy）

- [ ] 10.1 在 `src/spine/Seeing.Agent.Core/Configuration/SeeingAgentOptions.cs` 新增 `Tools.Exposure` 选项类：
  `PolicyMode`（`All|Auto|Tiered`，默认 `Auto`）、`PinnedTools`、`CatalogBudgetChars`（2000）、`CatalogLineMaxChars`（120）、`EscalateToolCount`（30）、`EscalateSchemaTokens`（8000）、`SearchDetail`（`name|summary|full`，默认 `summary`）、`SearchDefaultLimit`（10）、`SearchMaxLimit`（30）、`SearchFullMaxLimit`（3）
- [ ] 10.2 在 `src/abstractions/Seeing.Agent.Abstractions/Agents/AgentDefinition.cs` 新增 `ToolExposure` 覆盖字段（可空 `ToolExposurePolicyMode?` + 可空 `List<string>? PinnedTools`）
- [ ] 10.3 在 `src/primitives/Seeing.Session/Core/SessionScenarioOverride.cs` 的 `SessionToolsOverride` 新增 `PolicyMode`（`string?`，非枚举）：**原语层不得反向依赖 Abstractions**
- [ ] 10.4 在 `src/spine/Seeing.Agent.Core/Scenarios/ScenarioConfig.cs` 的 `ScenarioToolsConfig` 新增 `PolicyMode`（`string?`）：保持 `ScenarioDefinition` 的"纯字符串 id"既有约定，不引入枚举字段
- [ ] 10.5 在 `ScenarioConfig.ToDefinition` 与 `FromDefinition` 中透传 `PolicyMode` 字符串（不做解析，解析推迟到读取处）
- [ ] 10.6 实现四级优先级解析（会话 > Agent > Scenario > 全局），放 Hosting 的 `ToolExposureConfigResolver`；对每级 `string?` 做 `Enum.TryParse`（大小写不敏感），**非法值记 Warning 并回退到上一级**，不抛异常
- [ ] 10.7 写测试 `ToolExposureConfigResolverTests.ShouldResolvePriorityOrder`（四级各覆盖一次）
- [ ] 10.8 写测试 `ToolExposureConfigResolverTests.InvalidScenarioValue_ShouldWarnAndFallBackToGlobal`（`PolicyMode="tierd"` → 回退全局，且产生 Warning）
- [ ] 10.9 写测试 `ToolExposureConfigResolverTests.AgentEnum_ShouldOverrideScenarioString`
- [ ] 10.10 运行 `tests/primitives/Seeing.ConfigSchema.Tests` 确认 `Tools.Exposure` schema 条目已生成
- [ ] 10.11 更新 `docs/architecture/03-configuration-lifecycle.md`：配置表增加 `Tools.Exposure`、§4.2 结算公式加入 exposure 裁剪、§5 热重载表说明 Reload 路径

## 11. 会话发现态与升级状态（tool-discovery / tool-exposure-policy）

- [ ] 11.1 在 `src/primitives/Seeing.Session/Core/SessionData.cs` 新增 `public List<string> DiscoveredTools { get; set; } = new();` 与 `public bool ExposureEscalated { get; set; }`
- [ ] 11.2 新增 `src/hosting/Seeing.Agent.Hosting/Execution/ToolExposure/SessionToolDiscoveryStore.cs` 实现 `IToolDiscoveryStore`：`Get` 返回快照；`Add` 去重（**无上限淘汰**）；`Prune` 与可用集求交
- [ ] 11.3 确认 `SessionData.ClearMessages()`（`SessionData.cs:410-421`）不清 `DiscoveredTools` 与 `ExposureEscalated`
- [ ] 11.4 **必改**：在 `src/primitives/Seeing.Session/Management/SessionForker.cs:48-53` 的逐字段复制中增加 `DiscoveredTools` 与 `ExposureEscalated`（不加则 fork 后静默丢失）
- [ ] 11.5 写测试 `SessionToolDiscoveryStoreTests.ShouldBeMonotonic` / `ShouldDropVanishedIds` / `ShouldNotEvictOnSize`
- [ ] 11.6 写测试 `SessionForkerTests.Fork_ShouldInheritDiscoveryAndEscalation`
- [ ] 11.7 写测试 `SessionDataTests.OldSessionWithoutDiscoveryField_ShouldLoadAsEmpty`（用 `FileSessionStore` 反序列化不含该字段的 JSON）
- [ ] 11.8 写测试 `SessionDataTests.ClearMessages_ShouldKeepDiscoveryState`

## 12. 策略接入 SessionToolSchemaResolver（tool-exposure-policy）

- [ ] 12.1 在 `SettleAsync`（`SessionToolSchemaResolver.cs:126-141`）的 schema 生成之后插入：
  构建 `ToolCatalogEntry` 列表（复用同一次遍历的 `Description`/`ParametersSchema`/Hook 结果）→ 读发现态 → 解析生效模式 → 调用 `ExposureEscalationTracker` → 调 `IToolExposurePolicy.Plan` → 得到 `ToolExposurePlan`
- [ ] 12.2 合并 `InlineSchemas ∪ (已发现工具的 schema)`，按"pinned 声明序 + 发现序 + 其余 id 序"排列（D5）
- [ ] 12.3 返回 `SessionSchemaSettlement` 时携带 `CatalogEntries` / `InlineToolCount` / `DeferredToolCount` / `EstimatedDefinitionTokens` / `CatalogTokens`；`ResolveAsync` 同步映射到 `SessionToolSchemaResolution`
- [ ] 12.4 在 `SettleAsync` 读发现态后调用 `IToolDiscoveryStore.Prune(sessionId, availableIds)`（工具消失时裁剪）
- [ ] 12.5 升级状态写入：`ShouldEscalate` 为 true 时置 `session.ExposureEscalated = true` 并持久化
- [ ] 12.6 resolver **不得**新增对 `IToolManager` 之外的依赖读取；元信息必须来自同一次 schema 生成遍历
- [ ] 12.7 写测试 `SessionToolSchemaResolverTests.Tiered_ShouldExposeOnlyPinnedAndDiscovered`
- [ ] 12.8 写测试 `SessionToolSchemaResolverTests.Tiered_DiscoveredToolVanished_ShouldPruneNotThrow`
- [ ] 12.9 写测试 `SessionToolSchemaResolverTests.All_ShouldBehaveAsBefore`（`PolicyMode=All` 时 schema 集与改造前一致）
- [ ] 12.10 写测试 `SessionToolSchemaResolverTests.Auto_ShouldEscalate_WhenOverThreshold` 与 `ShouldNotDowngradeAfterToolsRemoved`
- [ ] 12.11 写测试 `SessionToolSchemaResolverTests.Ordering_ShouldBePinnedThenDiscovered`
- [ ] 12.12 在 `HostingServiceCollectionExtensions.cs:84-85` 附近注册 `IToolDiscoveryStore`、`AllToolExposurePolicy`、`TieredToolExposurePolicy`、`ToolExposurePolicySelector`、`ExposureEscalationTracker`、`ToolExposureConfigResolver`

## 13. 重构提示分节机制（tool-catalog-instructions）

- [ ] 13.1 在 `src/abstractions/Seeing.Agent.Abstractions/Prompts/IPromptSectionContributor.cs` 新增 `string Heading { get; }` 与 `PromptSectionPlacement Placement { get; }`
- [ ] 13.2 新增 `PromptSectionPlacement.cs`：`enum` 或 `record`，支持 `AppendAtEnd` 与 `After(string sectionName)` 两种
- [ ] 13.3 重构 `src/spine/Seeing.Agent.Core/Core/Prompts/PromptBuilder.cs`：删除 `SectionAnchors` 字典（`:19-26`）；`InjectSectionsAsync` 改为按 `Placement` 排序与定位；未自述 `Heading` 的贡献者记录 Error 日志并跳过（**不再静默**）
- [ ] 13.4 改造既有贡献者实现以提供 `Heading`/`Placement`：`ToolsPromptSectionContributor`、Skills、Agents、Environment 各自的实现（`Heading` 值沿用现有 `## Tools` 等）
- [ ] 13.5 写测试 `PromptBuilderPlacementTests.ShouldPlaceAfterNamedSection`
- [ ] 13.6 写测试 `PromptBuilderPlacementTests.ShouldAppendAtEnd_WhenPlacementIsAppendAtEnd`
- [ ] 13.7 写测试 `PromptBuilderPlacementTests.ShouldLogError_WhenHeadingMissing`（用 `ILogger` 替身断言至少一次 Error）
- [ ] 13.8 写测试 `PromptBuilderPlacementTests.NewSection_ShouldWorkWithoutBuilderChange`（用测试专用贡献者验证无需改 `PromptBuilder`）
- [ ] 13.9 运行 `tests/spine/Seeing.Agent.Tests/Prompts/` 全量，修复受影响的既有断言（`PromptBuilderSectionInjectionTests`、`PromptSectionContractsTests`、`DynamicPromptBuilderTests`）

## 14. 目录分节贡献者（tool-catalog-instructions）

- [ ] 14.1 在 `src/abstractions/Seeing.Agent.Abstractions/Prompts/PromptContext.cs` 新增通用槽 `IReadOnlyDictionary<string, string> Sections { get; set; }`（**不引入 ToolCatalogEntry**）
- [ ] 14.2 新增 `src/hosting/Seeing.Agent.Hosting/Prompts/ToolCatalogSectionContributor.cs`：`Heading = "## Tool Catalog"`、`Placement = AppendAtEnd`；仅当 `Sections["tool-catalog"]` 非空时返回内容
- [ ] 14.3 目录文本渲染：命名空间分组 + 计数标注 + "未加载工具不可直接调用" + `tool_search` 用法 + "检索结果自下一轮生效"
- [ ] 14.4 **必须注册 DI**：在 `HostingServiceCollectionExtensions` 中 `services.AddSingleton<IPromptSectionContributor, ToolCatalogSectionContributor>()`（现有注册点在 `ServiceCollectionExtensions.cs:792-794`，确认 Hosting 侧的注册入口）
- [ ] 14.5 在 `AgentExecutor.BuildRequestAsync` 构造 `PromptContext` 时从 `context` 取目录文本填入 `Sections["tool-catalog"]`
- [ ] 14.6 写测试 `ToolCatalogSectionContributorTests.ShouldRenderDeterministically`
- [ ] 14.7 写测试 `ToolCatalogSectionContributorTests.ShouldNotEmit_WhenNoDeferredTools`
- [ ] 14.8 写测试 `ToolCatalogSectionContributorTests.ShouldIncludeCallConstraintAndNextTurnNote`
- [ ] 14.9 写测试 `ToolCatalogSectionContributorTests.ShouldBeRegisteredInDI`（从容器解析 `IEnumerable<IPromptSectionContributor>` 断言包含该类型）

## 15. 检索元工具（tool-discovery）

- [ ] 15.1 新建项目 `src/capabilities/Seeing.Agent.Tools.ToolDiscovery/`（csproj 只引用 Abstractions + 原语），加入 `Seeing.Agent.slnx`
- [ ] 15.2 实现 `ToolSearchTool`：参数 `query`（string?）、`namespace`（string?）、`detail`（enum?）、`limit`（int?）、`offset`（int?，默认 0）
- [ ] 15.3 实现匹配与排序：tokenize（驼峰拆分 + 小写 + `s`/`es` 词形还原）→ 打分（精确 id 20 / id 单词 12 / id 子串 8 / 描述子串 4 / 全文 2）→ 零分过滤 → 分数降序、同分 id 序数升序
- [ ] 15.4 实现分页：`items = ranked.Skip(offset).Take(limit)`；返回 `total` / `returned` / `remaining` / `next`（`remaining>0 ? offset+returned : null`）
- [ ] 15.5 实现 `detail=full`：单页硬限 3（忽略更大 `limit`），对返回项写入发现态（经 `IToolDiscoveryStore`）
- [ ] 15.6 实现结果尾部引导：`remaining > 0` 优先建议"加 namespace / 换更精确关键词"，附 `offset=M` 兜底；`remaining == 0 && total > 0` 建议换更宽关键词；`full` 档剩余时额外说明"继续 full 加载会写入过多 schema"
- [ ] 15.7 实现 `offset` 越界：返回空结果、`total` 为真实总数、说明可用 offset 区间
- [ ] 15.8 实现空结果：未命中 + 可用命名空间及计数 + 建议，禁止返回空字符串
- [ ] 15.9 实现"下一轮生效"说明：`detail=full` 结果文本明确写出
- [ ] 15.10 新增 `ToolSearchEvent`（`src/abstractions/Seeing.Agent.Abstractions/Events/`）：`Query` / `Namespace` / `Detail` / `Offset` / `Total` / `Returned` / `Remaining` / `HitIds`
- [ ] 15.11 写测试 `ToolSearchToolTests`：三档 detail、namespace 限定、空 query 概览、未命中可操作性、精确 id 排第一
- [ ] 15.12 写测试 `ToolSearchToolTests.FirstPage_ShouldReportTotalAndNext`：命中 12、`limit=2` → `total=12/returned=2/remaining=10/next.offset=2`
- [ ] 15.13 写测试 `ToolSearchToolTests.SecondPage_ShouldContinueFromOffset`
- [ ] 15.14 写测试 `ToolSearchToolTests.LastPage_ShouldHaveNullNext` 与 `OffsetBeyondTotal_ShouldReturnEmptyWithRealTotal`
- [ ] 15.15 写测试 `ToolSearchToolTests.FullDetail_ShouldCapAtThreeRegardlessOfLimit`
- [ ] 15.16 写测试 `ToolSearchToolTests.FullDetail_ShouldWriteDiscovery`（`summary` 档位不写）
- [ ] 15.17 写测试 `ToolSearchToolTests.Guidance_ShouldPreferNarrowingOverPaging`
- [ ] 15.18 写测试 `ToolSearchToolTests.FullDetail_ShouldStateNextTurnEffect`
- [ ] 15.19 写测试 `ToolSearchToolTests.ShouldNotHaveDedicatedQuota`：连续调用 20 次（模拟），断言无"检索次数过多"拒绝路径；并断言代码中不存在针对 `tool_search` 的配额字段
- [ ] 15.20 写测试 `DiscoveryQuotaTests.SearchCalls_ShouldCountTowardMaxSteps`：`MaxSteps=3` 时，第 4 次检索不执行（由既有 step 上限终止，非检索专属逻辑）
- [ ] 15.21 实现 `ToolDiscoveryModule : ISeeingModule`：`Id = "tool-discovery"`、`ProvidedTools = ["tool_search"]`、`ConfigureServices` 注册工具、`ActivateAsync` 经 `IToolManager.RegisterToolAsync` 挂载、`DeactivateAsync` 注销
- [ ] 15.22 新建测试项目 `tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests/`，加入 `Seeing.Agent.slnx`
- [ ] 15.23 在 `samples/Seeing.Agent.WebUI/Program.cs` 注册模块；在 `src/spine/Seeing.Agent.Core/CapabilitySets/BuiltInCapabilitySets.cs` 的 `Full` 列表加入 `"tool-discovery"`
- [ ] 15.24 写测试 `ToolDiscoveryModuleTests.ShouldRegisterAndUnregisterToolSearch`

## 16. 可观测性（tool-exposure-policy / tool-schema-settlement）

- [ ] 16.1 扩展 `src/abstractions/Seeing.Agent.Abstractions/Events/SchemaSnapshotEvent.cs`：新增 `InlineToolCount` / `DeferredToolCount` / `EstimatedDefinitionTokens` / `CatalogTokens` / `ExposurePolicyMode`
- [ ] 16.2 在 `ExecutionRunner.cs:245-257` 填充新字段
- [ ] 16.3 写测试 `SchemaSnapshotEventTests.ShouldCarryExposureCounts`
- [ ] 16.4 在 `BuildExecutionContextAsync` 输出一行 Debug：`schema: mode={mode}, inline={n}, deferred={m}, schemaTokens={t}, catalogChars={c}`
- [ ] 16.5 写测试 `ExecutionRunnerTests.ShouldLogExposureMetrics`（`ILogger` 替身断言至少一次调用）
- [ ] 16.6 运行 `tests/spine/Seeing.Agent.Invariants.Tests` 全量（单点出口、Core 零工具、可见性不变量、`CoreZeroToolsTests`）
- [ ] 16.7 新增不变量测试 `SchedulerBypassSchemaTests`：Scheduler 直连路径与主链 schema 同源（复用 `AgentJobToolSchemasTests` 模式）

## 17. 验收

- [ ] 17.1 用 `full` 场景跑真实会话，记录 inline/deferred 计数、input tokens、冷门工具首调轮次
- [ ] 17.2 对比 `baseline.md`：51 内置工具场景 input tokens 降幅 ≥60%
- [ ] 17.3 构造 50 个 MCP 工具的仿真配置，验证降幅 ≥85%
- [ ] 17.4 写测试 `SchemaPrefixStabilityTests`：同一会话连续 20 轮请求，工具 id 序列前缀稳定
- [ ] 17.5 写测试 `EscalationStabilityTests`：MCP 中途增删工具，`auto` 模式只发生一次 `all→tiered` 升级
- [ ] 17.6 回退验证：`PolicyMode=All` 时所有新测试不触发延迟分支，`tools[]` 与改造前一致
- [ ] 17.7 写测试 `DeferredToolCall_ShouldStillGoThroughPermissionChain`：延迟工具调用进入审批
- [ ] 17.8 写测试 `Catalog_ShouldNotContainPermissionDecision`：目录文本不含"允许/拒绝"
- [ ] 17.9 更新 `docs/architecture/01-overview.md`：schema 段改为「resolver 单点结算 + exposure 裁剪阶段」，明示"单次执行内 schema 不变"
- [ ] 17.10 更新 `docs/architecture/02-modules.md` §3 模块表：新增 `Seeing.Agent.Tools.ToolDiscovery / tool-discovery`
- [ ] 17.11 更新 `docs/architecture/04-development-standards.md`：新增反模式「把 exposure 当权限」；新增评审项「schema 顺序稳定性」与「分节贡献者必须自述 Heading」
- [ ] 17.12 更新 `docs/architecture/06-compliance-audit.md`：schema 单点条目补 exposure 链；新增 `tool-discovery` 能力包依赖复核

## 18. 明确不做（Code Mode）

- [ ] 18.1 确认全仓无 `Code` 策略值、无 `run_code` 工具、无 ESM/脚本引擎依赖（`Directory.Packages.props` 不引入 Jint/ClearScript/Roslyn Scripting）
- [ ] 18.2 在 `docs/architecture/04-development-standards.md` 记录一条"未来若做 Code Mode"的前置约束：隔离形态必须先定（受限解释器 vs 独立 runner 子进程）；进程内 Roslyn 脚本因可 I/O/反射/退出进程而不可接受

## 19. 收尾

- [ ] 19.1 全量构建：`dotnet build Seeing.Agent.slnx` 零新增警告
- [ ] 19.2 全量测试：`tests/spine/Seeing.Agent.Tests`、`tests/hosting/Seeing.Agent.Hosting.Tests`、`tests/capabilities/Seeing.Agent.Mcp.Tests`、`tests/capabilities/Seeing.Agent.Tools.ToolDiscovery.Tests`、`tests/spine/Seeing.Agent.Invariants.Tests`、`tests/primitives/Seeing.Session.Tests`、`tests/primitives/Seeing.TokenEstimation.Tests`
- [ ] 19.3 `openspec validate tool-context-budgeting --strict`
- [ ] 19.4 新增 `docs/architecture/19-tool-context-budgeting-release-notes.md`：破坏性清单（MCP id 零兼容、同名 server 重复注册改报错、默认 `PolicyMode=Auto`）、开关与默认值、灰度与回退、已知边界（发现自下一轮生效、无配额限流）
- [ ] 19.5 在 `docs/architecture/README.md` 与根 `AGENTS.md` 的 WHERE TO LOOK 增加条目指向新 release notes
