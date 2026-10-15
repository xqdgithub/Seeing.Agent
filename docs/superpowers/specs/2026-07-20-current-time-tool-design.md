# Current Time Tool 设计

**日期:** 2026-07-20  
**状态:** 已批准

## 目标

在内置工具中新增获取当前时间的工具，供 Agent 在需要时间上下文时调用。

## 需求

- 默认返回本机本地当前时间
- 支持可选 IANA 时区参数（如 `Asia/Shanghai`）
- 固定返回 ISO 8601 格式（含偏移）
- 只读、无副作用，不请求权限

## 方案

独立 `CurrentTimeTool` 类，继承 `ToolBase`，经 DI 注册为 `ITool`，由现有 `ToolInvoker` 自动挂载。与现有 Read/WebFetch 等内置工具一致。

## 架构与组件

| 项 | 内容 |
|----|------|
| 文件 | `src/Seeing.Agent/Tools/BuiltIn/Time/CurrentTimeTool.cs` |
| 基类 | `ToolBase`（无需路径/权限辅助，不继承 `BuiltInToolBase`） |
| 注册 | `ServiceCollectionExtensions`：`services.AddSingleton<ITool, CurrentTimeTool>();` |
| 测试 | `tests/Seeing.Agent.Tests` 覆盖本地时间、合法时区、非法时区 |

## 工具约定

### 元数据

- **Id:** `current_time`
- **Description:** 获取当前时间；可选指定 IANA 时区，默认本机本地时区；返回 ISO 8601

### 参数 Schema

```json
{
  "type": "object",
  "properties": {
    "timezone": {
      "type": "string",
      "description": "IANA 时区 ID，例如 Asia/Shanghai、UTC、America/New_York；省略则使用本机本地时区"
    }
  }
}
```

无 `required` 字段。

### 执行逻辑

1. 读取可选 `timezone`
2. 未提供 → `TimeZoneInfo.Local`
3. 已提供 → `TimeZoneInfo.FindSystemTimeZoneById`（.NET 较新版本已支持 IANA ID）
4. `TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz)`，以 `"o"`（round-trip）格式输出

### 返回

- **成功:** `ToolResult` 文本为 ISO 8601，例如 `2026-07-20T18:05:00.1234567+08:00`
- **失败:** 未知时区 → `Failure("未知时区: {timezone}。请使用 IANA 时区 ID，例如 Asia/Shanghai")`

### 权限

不请求权限。

## 非目标

- 不支持自定义输出格式（人类可读中文等）
- 不返回额外 JSON 字段（如单独的 UTC / display）
- 不做时区列表枚举工具
