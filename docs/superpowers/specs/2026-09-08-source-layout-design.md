# 源码目录分类设计

**日期：** 2026-09-08  
**状态：** 已落地  
**约束：** 只改磁盘路径与 `.slnx` Folder；程序集 / 项目名不变；`tests/` 与 `src/` 镜像。

## 目标

解决 `src/`、`tests/` 数十个项目平铺、难按架构分层浏览与 Code Review 的问题，并与 `docs/architecture` 分层叙事对齐，降低「新项目随手丢在根下」的回潮概率。

## 分类

| 目录 | 含义 |
|------|------|
| `src/primitives/` | 原语：Session、TokenEstimation、ConfigSchema |
| `src/abstractions/` | 契约：Seeing.Agent.Abstractions |
| `src/spine/` | 脊柱：Seeing.Agent.Core |
| `src/hosting/` | Hosting + Host Shape（Web/Headless/Embed/Gateway） |
| `src/capabilities/` | 可插拔能力：Tools.*、Skills、Mcp、Llm.*、Agents、Scheduler、Memory、Acp、TokenBudget、IO.Local |
| `src/gateway/` | Gateway 协议、客户端、通道、Agent.Gateway 集成包 |
| `tests/{同名}/` | 镜像；另 `tests/apps/`、`tests/plugs/` |

## 非目标

- 不重命名 NuGet / 程序集  
- 不改变模块 id 或公共 API  
- 不强制拆 `capabilities/` 二级目录（可后续按需加 `tools/` 等）

## 维护规则

1. 新项目必须进入上表对应目录，并更新 `Seeing.Agent.slnx` 对应 Folder。  
2. `ProjectReference` 使用相对路径；搬家后以程序集名解析重写。  
3. 文档路径以 `docs/architecture/02-modules.md` 为准。  

## 验收

- `src/`、`tests/` 顶层仅分类目录  
- `dotnet build Seeing.Agent.slnx` 成功  
- 架构文档与 AGENTS/CLAUDE/README 入口路径一致  
