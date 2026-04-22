# Seeing.Agent.Plugins - 内置 Agent 实现

**用途:** 11 个预配置 Agent，支持规划/执行/评审/咨询等场景

---

## STRUCTURE

```
Agents/
├── PrometheusAgent.cs    # 规划 Agent（只规划不执行）
├── SisyphusAgent.cs      # 主 Agent（编排执行）
├── SisyphusJuniorAgent.cs # 简化版主 Agent（类别驱动）
├── OracleAgent.cs        # 咨询 Agent（架构/调试）
├── MetisAgent.cs         # 预规划顾问（范围澄清）
├── MomusAgent.cs         # 评审 Agent（计划审查）
├── ExploreAgent.cs       # 探索 Agent（代码搜索）
├── LibrarianAgent.cs     # 参考搜索 Agent（文档/示例）
├── HephaestusAgent.cs    # 工匠 Agent（前端/视觉）
├── AtlasAgent.cs         # 后台执行 Agent
└── MultimodalLookerAgent.cs # 多模态分析 Agent
```

---

## WHERE TO LOOK

| Agent | 用途 | 特点 |
|-------|------|------|
| **Sisyphus** | 主编排 | 并行委托、验证结果 |
| **Prometheus** | 规划 | 生成 `.sisyphus/plans/*.md` |
| **Oracle** | 咨询 | 只读，不修改代码 |
| **Momus** | 评审 | 检查计划完整性 |
| **Explore** | 搜索 | 后台并行 grep |
| **Librarian** | 参考 | GitHub/文档搜索 |

---

## CONVENTIONS

### Agent 模式分类
```csharp
AgentMode.Primary    // 主 Agent（完整工具集）
AgentMode.SubAgent   // 子 Agent（受限工具）
AgentMode.All        // 全权限（调试用）
```

### 调用委托模式
```csharp
// 主 Agent 委托子任务
task(subagent_type="explore", run_in_background=true, ...)
task(category="visual-engineering", load_skills=["frontend-ui-ux"], ...)
task(subagent_type="oracle", run_in_background=false, ...)
```

---

## ANTI-PATTERNS

| 禁止 | 原因 |
|------|------|
| Oracle 直接修改代码 | 保持只读咨询角色 |
| Prometheus 执行实现 | 只负责规划 |
| 跳过 Momus 评审 | 降低实现质量风险 |

---

## NOTES

- **加载方式**: `BuiltInAgents.GetBuiltInAgents()`
- **可扩展**: 实现 `IAgent` 或继承 `AgentBase`
- **配置驱动**: `.seeing/agents/*.md` 文件可定义新 Agent