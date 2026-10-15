# CLI 多实例运行管理设计

**日期:** 2026-08-06
**分支:** master
**目标:** `samples/Seeing.Agent.Cli`

## 背景与问题

当前 CLI 的进程管理存在两个缺陷：

1. **就绪检测目标错误**：`start webui` 启动的是独立 Blazor 应用（端口 5000），但就绪检测始终轮询 Gateway 管理 API（默认端口 8765 的 `/api/gateway/health`）。WebUI 不承载该端点，导致 `等待 webui 就绪` 必然超时失败。
2. **无法跨目录关闭 + 不支持多实例**：PID 文件按服务名存放在「启动时的工作目录」`.seeing/{service}.pid`。从其他文件夹执行 `stop` 时在自身目录找不到 PID，报「未在运行」。且固定端口（webui=5000、gateway=8765）和单例 PID 设计无法同时运行多个项目实例。

**需求确认（用户）：**
- 支持同时运行多个项目实例（每个项目各一套 webui/gateway）。
- `stop` 可从任意文件夹执行，存在多个实例时支持交互式选择关闭哪一个。
- 展示每个实例的启动文件夹。

## 方案：全局运行注册表（方案 A）

用一份全局 JSON 作为运行实例的唯一事实来源，存储实例元数据（PID、工作区、端口、启动时间），使 `start`/`stop`/`status` 在任何目录下都能定位与操作实例。

## 数据模型

注册表文件：`~/.seeing/instances.json`（用户级配置目录，与项目 `~/.seeing/seeing.json` 约定一致）。

```json
{
  "instances": [
    {
      "id": "webui-3f2a",
      "service": "webui",
      "pid": 1234,
      "workspaceRoot": "D:\\projA",
      "port": 5000,
      "startedAt": "2026-08-06T10:00:00Z"
    }
  ]
}
```

- `id`：唯一实例标识（`{service}-{随机后缀}`），用于 `stop` 定位。
- `service`：`webui` 或 `gateway`。
- `pid`：托管进程的 PID。
- `workspaceRoot`：启动时的工作目录（即启动文件夹）。
- `port`：该实例实际监听端口。
- `startedAt`：启动时间（UTC）。

## 组件设计

### 1. 端口分配

`start` 时确定端口：
- 默认端口：webui = 5000；gateway = 该工作区 `.seeing/seeing.json` 中 `SeeingAgent:Gateway:Port`，缺省 8765。
- 若默认端口被占用（进程监听中），自动探测下一个空闲端口（从默认端口逐步递增），避免多实例端口冲突。

### 2. `ServiceProcessManager` 重构

- `StartAsync(service, dllPath, port, extraArgs)`：启动进程，`WorkingDirectory = workspaceRoot`。
- 就绪检测：
  - webui：端口可达性（`GET /` 收到任意 HTTP 响应即就绪）。
  - gateway：`GET /api/gateway/health` 返回成功。
- 注册表操作：
  - 启动成功且就绪后写入注册表条目。
  - `StopAsync` / `PruneDeadInstances` 负责删除条目。
- 每次命令入口执行 `PruneDeadInstances()`：移除 PID 已失效的条目，保证注册表准确。

### 3. 命令交互

- `status`：列出全部实例，显示 `服务 / 文件夹 / 端口 / PID / 启动时间`。
- `stop <service>`：
  - 无该类型运行实例 → 提示「未在运行」。
  - 仅一个 → 直接关闭。
  - 多个 → 编号列表（显示文件夹 + 端口），交互式选择关闭哪一个。
- `start <service>`：与现有一致，但端口自动分配、注册到表。

### 4. 兼容与迁移

- 废弃旧 `{service}.pid` 机制；加载时删除历史遗留 pid 文件（`workspaceRoot/.seeing/webui.pid`、`gateway.pid` 及 `~/.seeing/*.pid`）。
- 保留 `ManagementApiClient` 关闭（`/api/admin/shutdown`）与状态查询（`/api/admin/status`）能力。

## 错误处理

- 端口分配失败（无可用端口）→ 报错退出。
- 进程启动失败 → 报错退出，不残留注册表条目。
- 就绪超时 → 报错并清理该实例条目与进程。
- `stop` 优雅关停失败 → kill 进程树。

## 测试

CLI 为独立示例项目，新增单元测试（如 `tests/Seeing.Agent.Cli.Tests`）覆盖：
- 注册表读写与 `PruneDeadInstances`。
- 端口分配（默认 + 占用递增）。
- 就绪检测分支（webui 可达性 vs gateway health）。
- `stop` 多实例选择逻辑。

## 范围

不在本次范围：修改 webui/gateway 服务本身、端口配置格式、跨平台部署。仅改动 `samples/Seeing.Agent.Cli`。