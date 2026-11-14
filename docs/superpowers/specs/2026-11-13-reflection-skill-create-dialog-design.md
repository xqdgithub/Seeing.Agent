# 复盘「生成 SKILL」弹窗与命名约束

**日期:** 2026-11-13  
**状态:** 已定稿（方案 1）

## 问题

复盘建议的 `target` 常为中文；记录页「生成 SKILL」一键直写项目级，既不选级别，也不让用户改名/正文。技能创建页要求 `^[a-z0-9]+(-[a-z0-9]+)*$`，复盘写入器此前只拦路径非法字符，中文名会落盘。

## 决策

| 项 | 选择 |
|---|---|
| 命名规则作用域 | 复盘写入器全路径（手动 + ApplyCreate）；不收紧平台 `SkillWriter` |
| 规则 | 与技能创建页一致：`^[a-z0-9]+(-[a-z0-9]+)*$`，长度 ≤ 64 |
| 手动流程 | Modal：可编辑名称 / 级别 / 描述 / 正文 |
| 级别默认 | 项目级（可改用户级） |
| 不合规预填 | 原样预填中文并标红；保存前必须改合规 |

## 架构

```
ReflectionRunsPage (Modal 草稿)
  → ReflectionRunOperations.CreateSkillAsync(runId, index, level, draft?)
    → ReflectionSuggestionsWriter.CreateFromSuggestionAsync(..., draft?)
      → TryValidateSkillName + 既有长度/注入/查重
      → ISkillWriter.CreateAsync
```

- **校验单点**：`ReflectionSuggestionsWriter.TryValidateSkillName`（公开静态），UI 与写入器共用口径。
- **草稿覆盖**：`ReflectionSkillDraft`（Name / Description / Content）；缺省时仍从 suggestion 取。
- **提示词**：要求 `target` 为英文 kebab，降低 ApplyCreate 被拒率。
- **描述 vs 备注**：`description` = SKILL 选型描述（必填，≤120 字，何时用/做什么）；
  `reason` = 备注（可选，会话介绍）；落盘只写 `description`，备注进 metadata `notes`。

## 非目标

- 不改 `SkillWriter` / 扫描器的宽松名称规则  
- 不抽共享 `SkillDraftEditor`、不跳转技能创建页  
- 不自动拼音/slug

## 测试

- Writer：中文 / 大写 / 空格名拒绝；合法 kebab 通过  
- Operations：草稿覆盖名称与描述后写入；级别显式用户级  
- 既有成功用例建议名改为合规 kebab
