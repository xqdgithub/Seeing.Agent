# Interval 生效时段（Windows）设计

**日期:** 2026-07-20  
**状态:** 已批准（待实现）  
**范围:** Seeing.Agent.Scheduler + WebUI Cron 编辑页 + `cron_create` Tool + cron Skills  
**注意:** 本文档仅本地参考，不纳入 Git 仓库。

---

## 1. 目标

为 `schedule.type = interval` 增加可选的 **生效时段集合** `windows`：

- 仅在时段内触发；
- 间隔以 **每个时段的起点** 为锚对齐；
- 支持多个时段；允许跨午夜；禁止交叠（端点相触允许）；
- 缺省起止：`start` → `00:00`，`end` → `23:59:59`；
- `windows` 为空 / 省略 → 行为与现有全天 interval 一致；
- 页面、Tool、Skill 同步支持；
- **展示与配置语义一律使用本地墙钟时间**（见第 5 节），不对用户暴露 UTC。

---

## 2. 已确认语义

| 决策点 | 选择 |
|--------|------|
| 中途创建/恢复时下一拍 | 按时段起点对齐：`start + n×interval` 中 ≥ now 的最早一拍 |
| 跨午夜时段 | 允许（`start > end`） |
| 空 `windows` | 无限制（全天），兼容现有行为 |
| 端点相触 | 允许（`end == next.start` OK）；禁止时间轴上重叠 |
| 实现路径 | 每时段注册 Quartz `DailyTimeIntervalTrigger`（方案 1） |
| Heartbeat `ActiveHours` | 本阶段不动 |

示例：时段 `09:00–23:00`、间隔 `40m`、当前 `10:05` → 下一拍 `10:20`。

多时段时，**每个时段独立**从各自 `start` 对齐，互不影响。

---

## 3. 数据模型

### 3.1 新增类型

```csharp
public class ScheduleWindow
{
    /// <summary>本地墙钟开始；缺省 00:00。格式 HH:mm 或 HH:mm:ss</summary>
    public string? Start { get; set; }

    /// <summary>本地墙钟结束；缺省 23:59:59。格式 HH:mm 或 HH:mm:ss</summary>
    public string? End { get; set; }
}
```

### 3.2 `ScheduleSpec` 扩展

```csharp
public class ScheduleSpec
{
    // ... 现有字段 ...
    /// <summary>仅 interval 有效。空/null = 全天。</summary>
    public List<ScheduleWindow>? Windows { get; set; }
}
```

JSON 示例（字段形状；下例两段交叠，校验应拒绝）：

```json
{
  "type": "interval",
  "every": "40m",
  "timezone": "Asia/Shanghai",
  "windows": [
    { "start": "09:00", "end": "12:00" },
    { "start": "14:00", "end": "18:00" }
  ]
}
```

### 3.3 校验规则（`ScheduleWindowsValidator`，引擎 / Tool / UI 共用）

1. 仅当 `type=interval` 时接受 `windows`；`cron`/`once` 带非空 `windows` → 校验失败。
2. `null` 或 `[]` → 通过（全天）。
3. 每项规范化：`Start ??= "00:00"`，`End ??= "23:59:59"`；解析为 `TimeSpan`。
4. 同日：`start ≤ end`；跨午夜：`start > end`。
5. 交叠检测：将每段在 **48h 轴**上展开；跨午夜拆成两段。统一用 **半开区间 `[start, end)`** 判断相交（`end == next.start` 不相交，即相触允许）。触发是否包含端点由 Quartz `endTimeOfDay` 决定，与交叠校验分开。
6. `every` 仍走现有 `ParseInterval`；`every` 必须为正。

---

## 4. 引擎调度（Quartz）

### 4.1 注册

对 `type=interval`：

1. 校验 `every` + `windows`。
2. **空 windows** → 保持现有单个 `SimpleSchedule` + `RepeatForever`。
3. **非空 windows** → 移除该 Job 上旧的 interval 相关 Trigger，再为每个逻辑窗口注册 `DailyTimeIntervalTrigger`：
   - **同日窗口**：`startTimeOfDay` / `endTimeOfDay` = 规范化后的起止，`interval` = `every`。
   - **跨午夜窗口**：拆成两个 Trigger：`start → 23:59:59` 与 `00:00:00 → end`。
4. Trigger Key：`{jobId}__w{i}`，跨午夜拆分加后缀 `_a` / `_b`。
5. 时区：`schedule.Timezone`（解析失败则 Local），Trigger 调用 `InTimeZone(...)`。
6. Misfire：与现有 interval 惯例对齐（`WithMisfireHandlingInstructionNextWithExistingCount`）。

### 4.2 对齐与查询

- 对齐由 `DailyTimeIntervalTrigger` 从 `startTimeOfDay` 起跳保证。
- `GetNextFireTime`：该 Job 全部 Trigger 的下一火时间取最小；**转成 schedule 时区本地时间后再对外展示**。
- Job 执行路径 **不再** 叠加 ActiveHours 式跳过；边界完全由 Trigger 决定。

### 4.3 更新 Job

修改 `every` / `windows` / `timezone` 时整组替换 Trigger。

---

## 5. 本地时间硬约束

1. **用户可见**：编辑、列表、详情、下次执行、Tool 返回、Skill 示例 → 一律墙钟本地时间（`Schedule.Timezone`，缺省 `TimeZoneInfo.Local`）。禁止直接展示 UTC / `Z` / `DateTimeKind.Utc`。
2. **配置语义**：`windows.start/end`、对齐、下一拍均按该时区本地时刻解释。
3. **持久化**：`windows` 只存时间字符串 + `timezone`；不存 UTC 瞬时。
4. **Quartz 内部**可用 UTC，但读写边界必须做时区转换；展示格式建议 `yyyy-MM-dd HH:mm:ss`（无 `Z`）。
5. **本需求触及的路径**：interval 新逻辑；顺带修正同页/同 API 上已有的「下次执行 / runAt」UTC 泄漏（若存在）。

---

## 6. UI / Tool / Skill

### 6.1 WebUI（`JobEditorModal` 等）

- 「间隔」类型下增加可选 **生效时段** 列表：开始/结束 `TimePicker`，可增删。
- 空列表文案：未设置则全天生效。
- 提交前规范化缺省起止；前端做交叠校验（与后端规则一致）。
- 列表/详情：展示 `every` + 时段摘要（本地时间）。

### 6.2 Tool（`cron_create`）

- `schedule.windows` 可选数组；Description/Schema 写明语义。
- 校验失败返回明确中文错误。

### 6.3 Skills（`cron-create`、必要时 `cron-management`）

- 补充 `windows` 示例与规则。

---

## 7. 测试计划

| 类别 | 用例 |
|------|------|
| 校验 | 缺省起止、空列表、相触允许、重叠拒绝、跨午夜合法、跨午夜与同日交叠拒绝、cron 带 windows 拒绝 |
| 下一拍 | 中途对齐（10:05→10:20）、多窗口、跨午夜拆 Trigger 后下一拍正确 |
| 本地时间 | 展示/API 不含 UTC 后缀；Timezone=Asia/Shanghai 时墙钟正确 |
| 回归 | 无 windows 的 interval 行为不变；cron/once 不受影响 |

---

## 8. 非目标

- 不改造 Heartbeat `ActiveHours` 为多时段。
- 不引入按星期过滤。
- 不改变 `interval` 的 `every` 语法。

---

## 9. 主要改动面

| 区域 | 文件/模块（预期） |
|------|-------------------|
| 模型 | `SchedulerModels.cs`（`ScheduleWindow`、`ScheduleSpec.Windows`） |
| 校验 | 新建 `ScheduleWindowsValidator` |
| 引擎 | `QuartzSchedulerEngine` |
| Tool | `CronCreateTool`、`CronScheduleParser` |
| Skill | `Skills/cron-create/SKILL.md` |
| UI | `JobEditorModal.razor`、列表/详情展示 |
| 测试 | Scheduler 单测 |
