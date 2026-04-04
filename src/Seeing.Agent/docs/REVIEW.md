# Seeing.Agent 架构评审报告

**评审日期:** 2026-04-04  
**更新日期:** 2026-04-04  
**评审范围:** 核心架构、Tool 系统、DI 注册

---

## 一、整体评价

| 维度 | 评分 | 说明 |
|------|------|------|
| **接口设计** | ⭐⭐⭐⭐ | 职责单一，依赖倒置，便于测试 |
| **扩展性** | ⭐⭐⭐⭐ | 支持 MCP、注解发现、装饰器链 |
| **线程安全** | ⭐⭐⭐⭐ | 全部使用并发集合 |
| **代码质量** | ⭐⭐⭐⭐ | 已重构，消除重复 |
| **文档完整** | ⭐⭐⭐⭐ | XML 注释详细，中文友好 |
| **测试覆盖** | ⭐⭐⭐⭐ | 30 个测试全部通过 |

**总评：⭐⭐⭐⭐ (4/5)** - 框架扎实，架构清晰

---

## 二、已修复问题

### ✅ 已修复：ToolInvoker 与 ToolRegistry 职责重叠

**原问题：**
```
┌──────────────────┐     ┌──────────────────┐
│   ToolInvoker    │     │   ToolRegistry   │
├──────────────────┤     ├──────────────────┤
│ _tools: Dict     │     │ _tools: Dict     │  ← 两份存储！
│ RegisterTool()   │     │ RegisterTool()   │  ← 重复方法
│ GetTool()        │     │ GetTool()        │  ← 重复方法
│ ExecuteAsync()   │     │ ExecuteAsync()   │  ← 重复方法
└──────────────────┘     └──────────────────┘
```

**修复方案：** 合并 ToolRegistry 到 ToolInvoker（已完成）

**当前状态：**
```
┌────────────────────────────────────────────────────┐
│                    ToolInvoker                      │
│                    (统一工具管理器)                  │
├────────────────────────────────────────────────────┤
│ ✅ 注册: RegisterTool, RegisterToolsFromType<T>    │
│ ✅ 注销: UnregisterTool                            │
│ ✅ 查询: GetTool, GetTools, GetToolsByTag,         │
│         GetToolsByCategory                         │
│ ✅ 执行: ExecuteAsync (支持 Hook 集成)             │
│ ✅ Schema: GetToolSchemas (LLM Function Calling)   │
│ ✅ 装饰器: 自动应用 IToolDecoratorRegistry          │
└────────────────────────────────────────────────────┘
```

---

## 三、当前问题清单

### 🟡 中等问题

| # | 问题 | 影响 | 建议 |
|---|------|------|------|
| 1 | HookManager 缺少移除能力 | 无法动态卸载 | 增加 `RemoveHandler()` |
| 2 | RuleEngine 用 ConcurrentBag | 无法删除规则 | 改用 ConcurrentDictionary |
| 3 | 参数验证不健壮 | 必需参数缺失时静默失败 | 增加 `ToolParameterValidator` |
| 4 | MCP 集成未完善 | MCP Server 连接为占位实现 | 完成 ModelContextProtocol 集成 |

### 🟢 轻微问题

| # | 问题 | 建议 |
|---|------|------|
| 5 | Schema 构建逻辑过长 | 拆分为 `SchemaBuilder` 类 |
| 6 | 日志级别不一致 | 统一规范（注册用 Info，执行用 Debug） |

---

## 四、架构优化建议

### 建议 1：HookManager 增加移除能力

```csharp
public bool RemoveHandler(IHookHandler handler)
{
    if (!_handlers.TryGetValue(handler.HookPoint, out var list))
        return false;
    
    lock (list)
    {
        return list.Remove(handler);
    }
}

public void ClearHandlers(string hookPoint)
{
    _handlers.TryRemove(hookPoint, out _);
}
```

### 建议 2：RuleEngine 改用 ConcurrentDictionary

```csharp
// 改用 ConcurrentDictionary 支持删除
private readonly ConcurrentDictionary<string, PermissionRule> _rules = new();

public void AddRule(PermissionRule rule)
{
    var key = $"{rule.Permission}:{rule.Pattern}";
    _rules[key] = rule;
}

public bool RemoveRule(string permission, string pattern)
{
    var key = $"{permission}:{pattern}";
    return _rules.TryRemove(key, out _);
}
```

### 建议 3：MCP 集成完善

当前 `DefaultMcpClientWrapper` 为占位实现，需要：
- 完成 `ModelContextProtocol` 包集成
- 或提供可插拔的 `IMcpClientFactory` 接口

---

## 五、重构优先级

### 第一优先级（短期）

| 任务 | 工作量 | 收益 |
|------|--------|------|
| RuleEngine 改用 ConcurrentDictionary | 2h | 支持规则删除 |
| HookManager 增加移除能力 | 1h | 支持动态卸载 |

### 第二优先级（中期）

| 任务 | 工作量 | 收益 |
|------|--------|------|
| 参数验证增强 | 3h | 提升健壮性 |
| MCP 集成实现 | 8h | 完整 MCP 支持 |

### 第三优先级（长期）

| 任务 | 工作量 | 收益 |
|------|--------|------|
| Session 持久化扩展 | 6h | 支持 Redis/DB |
| 工具文档生成 | 4h | 自动生成 API 文档 |

---

## 六、结论

Seeing.Agent 框架整体设计合理，接口职责清晰，线程安全处理到位。

**已修复的关键问题：**
- ✅ ToolInvoker/ToolRegistry 职责重叠 - 已合并

**待优化项：**
- HookManager 移除能力
- RuleEngine 删除规则
- MCP 集成完善

建议按优先级逐步优化，预计 1-2 周可完成关键改进。

---

**评审人:** Sisyphus  
**文档版本:** 1.1.0  
**更新说明:** ToolRegistry 已合并到 ToolInvoker