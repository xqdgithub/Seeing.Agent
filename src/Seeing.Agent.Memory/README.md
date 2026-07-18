# Seeing.Agent.Memory

基于文件的 AI Agent 记忆模块，参考 [ReMe](https://github.com/agentscope-ai/ReMe) 项目架构设计独立实现。

## 致谢

本项目的架构设计参考了 [ReMe](https://github.com/agentscope-ai/ReMe) 项目（Apache-2.0 License）：

> **ReMe** - Memory as File: 用文件管理 Agent 记忆，支持语义检索和知识图谱

主要参考内容：
- **Memory as File** 架构：使用 Markdown + YAML frontmatter 存储记忆
- **混合检索**：向量语义检索 + BM25 关键词检索 + RRF 融合
- **知识图谱**：基于 Wikilink (`[[...]]`) 的记忆关联
- **目录结构**：session/ (原始记录)、daily/ (浅加工)、digest/ (长期记忆)

**注意**：本项目为独立实现，未直接复制 ReMe 源代码。

## 功能特性

- **文件存储**：Markdown + YAML frontmatter，支持分块存储
- **混合检索**：向量 (sqlite-vec) + 关键词 (FTS5) + RRF 融合排序
- **知识图谱**：SQLite 实现，支持邻居查询和路径查找
- **Embedding 缓存**：SQLite 持久化，避免重复计算
- **成本控制**：Token Bucket 限流 + 配额管理
- **后台索引**：文件变更自动索引

## 目录结构

```
session/     原始会话记录
daily/       每日浅加工记忆
digest/      长期记忆 (LLM 整合)
```

## 文件格式

```yaml
---
id: abc123
type: session
title: 对话标题
tags: [tag1, tag2]
importance: 0.8
confidence: 1.0
created_at: 2025-01-15T10:30:00+08:00
---

# 记忆内容

这里是 Markdown 格式的记忆内容...

相关记忆: [[另一个记忆ID]]
```

## 依赖

- .NET 10.0
- SQLite + sqlite-vec (向量检索) + FTS5 (全文检索)
- YamlDotNet
