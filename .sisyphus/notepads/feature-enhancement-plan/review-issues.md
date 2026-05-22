# 实现审核报告 - 需修复问题清单

**审核日期:** 2025-01-22
**审核结果:** 发现 17 个问题，其中 4 个严重、8 个中等、5 个轻微

---

## 🔴 严重问题（必须修复）

### ISSUE-001: GitCommitTool 语法错误
**位置:** `src/Seeing.Agent/Git/Tools/GitCommitTool.cs:17`
**问题:** 
```csharp
public string Description = "Commit changes to the repository";  // 错误：使用了赋值
```
**应改为:**
```csharp
public string Description => "Commit changes to the repository";  // 正确：使用表达式体
```
**影响:** 编译警告/错误，接口属性无法正确实现

---

### ISSUE-002: Snapshot ModifiedLines 计算错误
**位置:** `src/Seeing.Agent/Core/Snapshot/SnapshotManager.cs:150`
**问题:** 
```csharp
ModifiedLines = diffs.Count(d => d.Operation == DiffOperation.Equal),  // 错误：Equal是未修改的行
```
**应改为:** ModifiedLines 应计算真正的修改行数，或重命名为 UnchangedLines

---

### ISSUE-003: MCP OAuth 未完整实现
**位置:** `src/Seeing.Agent/MCP/OAuth/McpOAuthProvider.cs`
**问题:** 
- Line 50: 授权URL使用占位符 `https://example.com/oauth/authorize`
- Line 81: Token交换返回 `AccessToken = "placeholder"`
- Line 127: RefreshToken 返回 "not fully implemented"
- 缺少 authorization_endpoint、token_endpoint 配置传入

**建议:** 
1. 添加 `McpOAuthEndpointsConfig` 类承载端点URL
2. 在 `StartAuthAsync` 接收端点配置参数
3. 实现完整的 Token 交换 HTTP POST

---

### ISSUE-004: BackgroundTaskManager 缺少 System.Reactive 包引用
**位置:** `src/Seeing.Agent/Core/Background/BackgroundTaskManager.cs`
**问题:** 
- 使用 `Subject<BackgroundTaskProgress>` 和 `Subject<string>` 
- 项目未引用 `System.Reactive` NuGet包
- `ReportProgress` 和 `ReportOutput` 方法是 private，外部无法调用进度报告

**应修复:**
1. 添加 `<PackageReference Include="System.Reactive" Version="6.0.0" />` 到 csproj
2. 添加公开的 `IBackgroundTaskProgress` 实现类供任务使用

---

## 🟡 中等问题（影响功能完整性）

### ISSUE-005: AgentGenerator 未注册到 IAgentRegistry
**位置:** `src/Seeing.Agent/Core/Generation/AgentGenerator.cs:92`
**问题:** 生成的 Agent 只存内存字典 `_definitions`，未调用 `IAgentRegistry.RegisterAgentAsync`
**影响:** 生成的 Agent 无法被 AgentRegistry 发现和执行

---

### ISSUE-006: SkillPuller 不是真正的 Git 克隆
**位置:** `src/Seeing.Agent/Skills/Pulling/SkillPuller.cs`
**问题:** `PullFromGitAsync` 只下载 raw 文件，不支持：
- 私有仓库认证
- 多文件/目录拉取
- 非 GitHub 仓库（GitLab、Bitbucket）

---

### ISSUE-007: PlanManager ListPlansAsync 不扫描文件
**位置:** `src/Seeing.Agent/Tools/BuiltIn/Plan/PlanManager.cs:102-118`
**问题:** 只返回内存 `_plans`，启动时不会加载已存在的计划文件
**影响:** 重启后丢失持久化的计划

---

### ISSUE-008: SessionForker 缺少快照集成
**位置:** `src/Seeing.Session/Management/SessionForker.cs`
**问题:** 计划要求 "为跟踪的文件创建快照"，但未使用 `ISnapshotManager`
**影响:** Fork 后文件状态无法恢复

---

### ISSUE-009: SessionReverter 缺少文件恢复
**位置:** `src/Seeing.Session/Management/SessionReverter.cs`
**问题:** 计划要求 "恢复文件快照"，但只截断消息列表
**影响:** 无法真正恢复到历史状态

---

### ISSUE-010: SessionSharer 无自动清理
**位置:** `src/Seeing.Session/Management/SessionSharer.cs`
**问题:** 分享有过期时间 `ExpiresAt`，但无后台清理机制
**影响:** 过期分享文件会无限累积

---

### ISSUE-011: DiffCalculator 不是 diff-match-patch
**位置:** `src/Seeing.Agent/Core/Snapshot/DiffCalculator.cs`
**问题:** 只实现了简单 LCS 算法，不是 Google diff-match-patch
**影响:** 
- Diff 粒度是行级，不是字符级
- 不支持语义清理
- 大文件性能可能较差

---

## 🟢 轻微问题

### ISSUE-012: OAuth Storage 跨平台兼容
**位置:** `src/Seeing.Agent/MCP/OAuth/McpOAuthStorage.cs`
**问题:** 使用 Windows DPAPI，非 Windows 平台无法运行
**建议:** 提供跨平台加密方案（如 AES 加密）

---

### ISSUE-013: 测试覆盖不足
**位置:** `tests/Seeing.Agent.Tests/`
**问题:** 大部分测试只验证属性默认值，未测试核心业务逻辑

---

### ISSUE-014: Git 默认分支假设
**位置:** `src/Seeing.Agent/Skills/Pulling/SkillPuller.cs:49`
**问题:** 假设默认分支是 `main`，部分仓库可能用 `master`
**建议:** 支持 `default_branch` API 查询或配置

---

### ISSUE-015: Template 变量命名不一致
**位置:** `src/Seeing.Agent/Core/Generation/AgentGenerator.cs`
**问题:** 模板用 `{{Name}}` 大写，但变量传入可能用小写 `name`
**建议:** 统一为小写或支持大小写不敏感

---

## 修复优先级

| 优先级 | 问题编号 | 说明 |
|--------|----------|------|
| P0 (立即) | ISSUE-001, ISSUE-004 | 编译/运行问题 |
| P1 (本周) | ISSUE-002, ISSUE-003 | 核心功能缺失 |
| P2 (下周) | ISSUE-005, ISSUE-007, ISSUE-008, ISSUE-009 | 功能完整性 |
| P3 (后续) | ISSUE-006, ISSUE-010, ISSUE-011, ISSUE-012-015 | 优化改进 |

---

## 下一步行动

请执行代理（Sisyphus）按优先级修复：
1. 先修复 ISSUE-001（语法错误）和 ISSUE-004（包引用）
2. 然后修复 ISSUE-002（ModifiedLines 计算）和 ISSUE-003（OAuth 实现）
3. 最后完善功能完整性问题