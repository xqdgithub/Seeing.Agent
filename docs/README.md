# Seeing.Agent 文档

本目录集中存放项目文档。模块级 README 仍保留在对应 `src/`、`samples/` 目录中。

## 用户与集成

| 文档 | 说明 |
|------|------|
| [Gateway 总览](gateway/README.md) | 外部通讯架构、快速启动、协议要点 |
| [ACP 集成](acp/integration.md) | Agent Client Protocol 透传与 acp 工具委派 |

## 架构与设计

| 文档 | 说明 |
|------|------|
| [框架架构](architecture/ARCHITECTURE.md) | 分层设计、核心模块、配置结构 |
| [LLM 模块设计](architecture/LLM_MODULE_DESIGN.md) | Provider、模型路由与客户端设计 |
| [Extension 扩展](architecture/EXTENSION.md) | 插件加载、配置层级与开发指南 |

## 开发参考

| 文档 | 说明 |
|------|------|
| [架构评审](development/REVIEW.md) | 核心架构评审与已知改进点 |
| [Provider 流程评审](development/PROVIDER_FLOW_REVIEW.md) | LLM Provider 调用链分析 |

## 历史实施计划

`plans/` 目录保存已完成或进行中的功能实施计划，供维护者查阅，非用户使用文档。

| 文档 | 说明 |
|------|------|
| [MCP 重构计划](plans/MCP_REFACTOR_PLAN.md) | MCP 模块重构方案 |
| [Hook 重构](plans/hook-refactor-2026-04-28.md) | Hook 系统重构计划 |
| [NewTui 实施计划](plans/IMPLEMENTATION_PLAN.md) | Spectre TUI 示例实施 |
| [事件渲染优化](plans/EVENT_RENDERING_OPTIMIZATION_PLAN_FINAL.md) | WebUI 事件渲染优化（终稿） |
| [消息渲染](plans/MESSAGE_RENDERING_IMPLEMENTATION_PLAN.md) | WebUI 消息渲染实施 |

## 模块 README

| 模块 | 路径 |
|------|------|
| Seeing.Gateway | [src/Seeing.Gateway/README.md](../src/Seeing.Gateway/README.md) |
| Seeing.Gateway.Client | [src/Seeing.Gateway.Client/README.md](../src/Seeing.Gateway.Client/README.md) |
| Seeing.Agent.Gateway | [src/Seeing.Agent.Gateway/README.md](../src/Seeing.Agent.Gateway/README.md) |
| Seeing.Gateway.WeCom | [src/Seeing.Gateway.WeCom/README.md](../src/Seeing.Gateway.WeCom/README.md) |
| Gateway Server 示例 | [samples/Seeing.Gateway.Server/README.md](../samples/Seeing.Gateway.Server/README.md) |

## AI 辅助开发

仓库根目录 [AGENTS.md](../AGENTS.md) 为 Cursor / Agent 项目知识库，包含约定、反模式与快速定位表。
