# Current Time Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增内置工具 `current_time`，支持可选 IANA 时区，返回 ISO 8601 当前时间。

**Architecture:** 独立 `CurrentTimeTool : ToolBase`，DI 注册为 `ITool`，由现有 `ToolInvoker` 自动挂载。无权限、无外部依赖。

**Tech Stack:** .NET 10、xUnit、FluentAssertions、`TimeZoneInfo`

## Global Constraints

- Tool Id 固定为 `current_time`
- 可选参数 `timezone`（IANA ID）；缺省用 `TimeZoneInfo.Local`
- 成功输出为 `"o"` round-trip ISO 8601
- 未知时区返回 `Failure`，文案含示例 `Asia/Shanghai`
- 不请求权限

---

### Task 1: CurrentTimeTool + 单元测试 + DI 注册

**Files:**
- Create: `src/Seeing.Agent/Tools/BuiltIn/Time/CurrentTimeTool.cs`
- Create: `tests/Seeing.Agent.Tests/Tools/CurrentTimeToolTests.cs`
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`（using + `AddSingleton<ITool, CurrentTimeTool>()`）

**Interfaces:**
- Consumes: `ToolBase`（`Success`/`Failure`/`GetStringArgument`）、`ToolContext`、`ToolResult`
- Produces: `CurrentTimeTool`（`Id = "current_time"`）

- [x] **Step 1: Write failing tests**

```csharp
// tests/Seeing.Agent.Tests/Tools/CurrentTimeToolTests.cs
public class CurrentTimeToolTests
{
    [Fact]
    public async Task ExecuteAsync_WithoutTimezone_ShouldReturnIso8601LocalTime()
    {
        var tool = new CurrentTimeTool(NullLogger<CurrentTimeTool>.Instance);
        var args = JsonSerializer.SerializeToElement(new { });
        var before = DateTimeOffset.Now.AddSeconds(-2);
        var result = await tool.ExecuteAsync(args, new ToolContext());
        var after = DateTimeOffset.Now.AddSeconds(2);
        result.Success.Should().BeTrue();
        var parsed = DateTimeOffset.Parse(result.Output!);
        parsed.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        parsed.Offset.Should().Be(TimeZoneInfo.Local.GetUtcOffset(parsed));
    }

    [Fact]
    public async Task ExecuteAsync_WithAsiaShanghai_ShouldReturnOffsetPlusEight()
    {
        var tool = new CurrentTimeTool(NullLogger<CurrentTimeTool>.Instance);
        var args = JsonSerializer.SerializeToElement(new { timezone = "Asia/Shanghai" });
        var result = await tool.ExecuteAsync(args, new ToolContext());
        result.Success.Should().BeTrue();
        var parsed = DateTimeOffset.Parse(result.Output!);
        parsed.Offset.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidTimezone_ShouldFail()
    {
        var tool = new CurrentTimeTool(NullLogger<CurrentTimeTool>.Instance);
        var args = JsonSerializer.SerializeToElement(new { timezone = "Not/A_Real_Zone" });
        var result = await tool.ExecuteAsync(args, new ToolContext());
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Not/A_Real_Zone");
        result.Error.Should().Contain("Asia/Shanghai");
    }
}
```

- [x] **Step 2: Run tests — expect FAIL**（类型不存在）

```bash
dotnet test tests/Seeing.Agent.Tests --filter CurrentTimeToolTests
```

- [x] **Step 3: Implement tool + register DI**

```csharp
// CurrentTimeTool.cs — Id=current_time, optional timezone, ConvertTime + ToString("o")
// ServiceCollectionExtensions — using Seeing.Agent.Tools.BuiltIn.Time;
//   services.AddSingleton<ITool, CurrentTimeTool>();
```

- [x] **Step 4: Run tests — expect PASS**

- [ ] **Step 5: Commit**（仅当用户明确要求时）
