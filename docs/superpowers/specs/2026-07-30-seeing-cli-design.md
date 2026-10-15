# Seeing.Agent.Cli 设计规格

**日期：** 2026-07-30
**状态：** 待审核

---

## 概述

在 `samples/Seeing.Agent.Cli/` 下新增 `seeing-cli` 命令行管理工具，提供代理系统相关的配置管理、服务启停、状态查询等能力。CLI 与 WebUI 共享同一套 DI 容器和服务入口，配置操作直接操作内部服务，服务管理通过本地 HTTP 管理 API 完成。不包含 TUI 或代理交互功能。

**核心原则：**
- 和 WebUI 共用 DI 容器和服务注册链
- 配置类命令直接操作内部服务（不依赖 Gateway 运行）
- 服务生命周期命令通过 Gateway 管理 API（`:8765/admin/*`）完成
- 使用 `System.CommandLine` 作为命令行解析框架

---

## 命令总览

```
seeing-cli -p "<prompt>" [--yolo] [--stream]  一次性执行提示词并返回结果（可加入脚本工作流）
seeing-cli start webui|gateway    启动服务（后台子进程）
seeing-cli stop [webui|gateway]   停止服务
seeing-cli status                 查看服务运行状态
seeing-cli config show|set|init   配置管理
seeing-cli agent list|show        代理管理
seeing-cli job list|run|disable|enable  定时任务管理
seeing-cli session list|delete|show     会话管理
```

> 一次性执行详见 `docs/superpowers/specs/2026-10-12-cli-one-shot-prompt-design.md`。

---

## 项目结构

```
samples/Seeing.Agent.Cli/
├── Program.cs                      入口，命令行路由
├── Seeing.Agent.Cli.csproj         net10.0, 引用 Seeing.Agent.App + System.CommandLine
├── Commands/
│   ├── StartCommand.cs             启动 WebUI / Gateway（Process.Start）
│   ├── StopCommand.cs              通过管理 API 停止服务
│   ├── StatusCommand.cs            通过管理 API 查询运行状态
│   ├── ConfigCommand.cs            读写 seeing.json
│   ├── AgentCommand.cs             列出/查看代理
│   ├── JobCommand.cs               定时任务管理
│   └── SessionCommand.cs           会话管理
├── Services/
│   ├── ServiceProcessManager.cs    子进程生命周期管理（启动、PID 跟踪、健康检查等待）
│   └── ManagementApiClient.cs      封装对 Gateway 管理 API 的 HTTP 调用
└── Infrastructure/
    └── CliServiceBootstrap.cs      初始化 DI 容器（复用 WebUI 注册链）
```

### 依赖关系

- 引用 `Seeing.Agent.App`（ChatOrchestrator、ExecutionJobService 等核心服务）
- 引用 `System.CommandLine`（命令解析与路由）
- 不引用 Blazor、AntDesign、Circuit 等 UI 专用包

### DI 初始化（`CliServiceBootstrap`）

复用 WebUI 的注册链，但剔除 UI 专用部分：

```csharp
services.AddSeeingAgent(configuration);
services.AddSeeingAcp();
services.AddSeeingScheduler();
services.AddSeeingGatewayServer(configuration);
services.AddChatOrchestrator();
services.AddMemoryServices();
// 不加: Blazor、AntDesign、Circuit、MessageRendering、信号器相关
```

配置类命令加载完整 DI 容器后执行操作。服务管理命令仅需 `ManagementApiClient`（轻量 HTTP 客户端），不需要完整 DI。

---

## 服务进程管理（`ServiceProcessManager`）

CLI 通过 `Process.Start` 启动 WebUI/Gateway 作为后台子进程，启动后通过管理 API 控制。

### 启动流程

```
seeing-cli start webui
  → 找到 WebUI 的 dll 路径（和 CLI 同级目录或配置指定）
  → Process.Start("dotnet", "Seeing.Agent.WebUI.dll --urls http://0.0.0.0:5000")
  → 写入 PID 文件到 .seeing/webui.pid
  → 轮询 GET http://127.0.0.1:8765/api/gateway/health 最多 30 秒
  → 就绪后打印 "WebUI 已启动: http://0.0.0.0:5000"
```

`start gateway` 同理，启动 `Seeing.Gateway.Server.dll`，同样写 PID 文件并等待健康检查。

### 停止流程

```
seeing-cli stop
  → POST http://127.0.0.1:8765/api/admin/shutdown
  → Gateway 收到请求 → 触发 IHostApplicationLifetime.StopApplication()
  → 等待进程退出（最多 10 秒），超时则 Process.Kill()
  → 清理 PID 文件
```

### 状态查询

```
seeing-cli status
  → 检查 PID 文件是否存在、进程是否存活
  → 如果存活 → GET http://127.0.0.1:8765/api/admin/status
  → 格式化输出运行状态表格
```

### 关键点

- WebUI 启动时会连带启动 Gateway（因为 `AddSeeingGatewayServer`），CLI 统一通过 Gateway 管理端口通信
- 端口信息从 `.seeing/seeing.json` 的 `Gateway.Port` 读取
- 如果用户单独启动 `seeing-cli start gateway`，只启动 Gateway 无 WebUI
- 崩溃恢复：启动时如果发现 PID 文件已存在但进程已死，自动清理后重新启动

---

## 管理 API 端点（`/api/admin/*`）

在现有 `GatewayEndpoints` 中新增管理端点组，在 `GatewayHost.StartAsync` 中注册。CLI 通过 `ManagementApiClient` 调用。

### 端点列表

| 方法 | 路径 | 功能 |
|------|------|------|
| `GET` | `/api/admin/status` | 返回运行状态详情 |
| `POST` | `/api/admin/shutdown` | 优雅关闭 Gateway 进程 |
| `GET` | `/api/admin/sessions` | 列出当前活跃会话 |
| `GET` | `/api/admin/jobs` | 列出定时任务及上次执行状态 |

### `GET /api/admin/status` 响应

```json
{
  "status": "running",
  "uptime": "2h 15m",
  "gatewayPort": 8765,
  "webuiRunning": true,
  "activeSessions": 3,
  "activeExecutions": 1,
  "schedulerRunning": true,
  "memoryUsageMb": 156
}
```

### `POST /api/admin/shutdown`

调用 `IHostApplicationLifetime.StopApplication()` 触发优雅关闭。返回 `{ "message": "shutting down" }`，CLI 端等待进程退出。

### 实现

复用已有服务实例获取状态数据：
- `GatewayRunTracker` → activeExecutions
- `ISessionManager` → activeSessions
- `ScheduleManager` → schedulerRunning

端点注册在 `GatewayHost.StartAsync` 的 `app.MapGatewayEndpoints()` 调用链中追加 `app.MapAdminEndpoints()`。

---

## 配置管理（`ConfigCommand`）

CLI 直接加载 DI 容器，通过 `UnifiedConfigManager` / `IConfigSectionStore` 读写配置，不依赖 Gateway 是否运行。

### 命令

```
seeing-cli config show [section]      查看全部或指定配置段
seeing-cli config set <path> <value>  设置配置值
seeing-cli config init                初始化工作区（生成 .seeing/ 目录结构）
```

### `config show`

读取合并后的 `seeing.json`（用户级 + 项目级），按段格式化输出。可以指定 section 只看一部分。

底层通过 `UnifiedConfigManager.GetSectionAsync(section)` 获取数据。

### `config set <path> <value>`

点号路径定位配置项，调用 `UnifiedConfigManager.SetValueAsync(section, path, value)` 后自动保存到对应层级的 `seeing.json`。

### `config init`

在当前目录创建 `./.seeing/` 骨架：
```
./.seeing/
  seeing.json        (项目级配置模板)
  agents/            (代理定义目录)
  sessions/          (会话存储)
```

---

## 代理管理（`AgentCommand`）

通过 `AgentManager` / `IAgentRegistry` 操作，不依赖 Gateway。

```
seeing-cli agent list                列出所有代理
seeing-cli agent show <name>         查看代理详情
```

`agent list` 输出表格：Name、Mode、Runtime、Model、Status。
`agent show <name>` 输出详情：权限规则、允许/禁止的工具列表、系统提示词摘要、ACP 后端配置等。

---

## 定时任务管理（`JobCommand`）

复用 `SchedulerCommands` 已有逻辑，通过 `IScheduleManager` 操作，不依赖 Gateway。

```
seeing-cli job list                  列出所有定时任务
seeing-cli job run <jobId>           立即执行一次
seeing-cli job disable <jobId>       暂停
seeing-cli job enable <jobId>        恢复
```

`job list` 输出：JobId、Name、Schedule、Status、LastRun、NextRun。

---

## 会话管理（`SessionCommand`）

通过 `ISessionManager` 操作，不依赖 Gateway。

```
seeing-cli session list              列出所有会话
seeing-cli session delete <id>       删除指定会话
seeing-cli session show <id>         查看会话摘要
```

`session list` 输出：SessionId、Agent、MessageCount、CreatedAt、LastActive。
`session show <id>` 输出摘要：消息数、所选代理、创建时间、最近活动时间、压缩状态。

---

## 测试策略

- **单元测试：** 对 `ServiceProcessManager`、`ManagementApiClient`、`CliServiceBootstrap` 进行单元测试，mock 外部依赖（Process、HttpClient）
- **集成测试：** 对 `ConfigCommand`、`AgentCommand`、`SessionCommand` 编写集成测试，使用真实 DI 容器但使用临时 `.seeing` 目录
- **端到端测试：** 启动 WebUI 进程 → `status` → `stop` 的完整流程测试

---

## 错误处理

- **端口冲突：** 启动时检测端口是否被占用，给出明确提示
- **进程已存在：** 检测 PID 文件 → 进程存活则提示"服务已在运行"，进程已死则清理后启动
- **超时：** 健康检查超时（30 秒）后提示启动失败，建议检查日志
- **API 不可达：** `status`/`stop` 命令在 Gateway 不可达时回退到仅显示 PID 文件信息
- **配置写入失败：** 文件权限不足或 JSON 格式错误时给出具体错误信息
