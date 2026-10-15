# WebUI Tool Call Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 重做 WebUI 工具调用展开详情，使参数显示真实键值、结果显示完整内容，并支持键值表/JSON、可读/原始切换与复制。

**Architecture:** 将 JSON 解析与展示格式化抽到可单测的静态 helper `ToolCallDisplayFormatter`；`ToolCallMessageComponent` 只负责收起行（不变）与展开区 UI 状态（视图模式、展开全部、复制）。不改 Session/Event 数据管道。

**Tech Stack:** Blazor Server、AntDesign、System.Text.Json、xUnit + FluentAssertions、现有 `IJSRuntime` 剪贴板 API。

## Global Constraints

- 不新增 NuGet / 语法高亮库
- 不改 `EventStreamHandler`、`SessionToolCall`、`ToolCallViewModel` 数据字段语义
- 收起行行为保持现状
- `todowrite` 继续仅渲染 `TodoCard`
- 空参数（null / `{}`）或不展示结果时：对应分区不渲染
- 用户明确要求：**不要自动 git commit**；计划中的 commit 步骤一律跳过，除非用户另行要求
- 实现以 spec 为准：`docs/superpowers/specs/2026-07-18-webui-tool-call-display-design.md`

## File Structure

| 文件 | 职责 |
|------|------|
| `samples/Seeing.Agent.WebUI/Helpers/ToolCallDisplayFormatter.cs` | 参数解析、JSON 美化、可读内容、长值判断、复制用文本 |
| `samples/Seeing.Agent.WebUI/Components/Messaging/ToolCallMessageComponent.razor` | 展开区 UI + 交互状态 |
| `samples/Seeing.Agent.WebUI/wwwroot/css/tool-components.css` | 展开详情样式 |
| `tests/Seeing.Agent.WebUI.Tests/Helpers/ToolCallDisplayFormatterTests.cs` | formatter 单元测试 |

---

### Task 1: ToolCallDisplayFormatter + 单元测试

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Helpers/ToolCallDisplayFormatter.cs`
- Create: `tests/Seeing.Agent.WebUI.Tests/Helpers/ToolCallDisplayFormatterTests.cs`

**Interfaces:**
- Consumes: `System.Text.Json`
- Produces:
  - `record ToolCallParameterEntry(string Name, string Value, bool IsLong)`
  - `static class ToolCallDisplayFormatter` with methods below

- [ ] **Step 1: 写失败测试**

创建 `tests/Seeing.Agent.WebUI.Tests/Helpers/ToolCallDisplayFormatterTests.cs`:

```csharp
using FluentAssertions;
using Seeing.Agent.WebUI.Helpers;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Helpers;

public class ToolCallDisplayFormatterTests
{
    [Fact]
    public void TryParseParameters_Object_ShouldReturnNameAndValues()
    {
        var json = """{"filePath":"E:\\\\a.cs","content":"hello"}""";

        var ok = ToolCallDisplayFormatter.TryParseParameters(json, out var entries);

        ok.Should().BeTrue();
        entries.Should().HaveCount(2);
        entries[0].Name.Should().Be("filePath");
        entries[0].Value.Should().Be(@"E:\a.cs");
        entries[1].Name.Should().Be("content");
        entries[1].Value.Should().Be("hello");
        entries[1].IsLong.Should().BeFalse();
    }

    [Fact]
    public void TryParseParameters_NestedObject_ShouldSerializeCompactJson()
    {
        var json = """{"meta":{"a":1}}""";

        ToolCallDisplayFormatter.TryParseParameters(json, out var entries);

        entries.Should().ContainSingle();
        entries[0].Name.Should().Be("meta");
        entries[0].Value.Should().Be("""{"a":1}""");
    }

    [Fact]
    public void TryParseParameters_EmptyObject_ShouldReturnFalse()
    {
        ToolCallDisplayFormatter.TryParseParameters("{}", out var entries)
            .Should().BeFalse();
        entries.Should().BeEmpty();
    }

    [Fact]
    public void TryParseParameters_InvalidJson_ShouldReturnFalse()
    {
        ToolCallDisplayFormatter.TryParseParameters("not-json", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLongValue_Over400Chars_ShouldBeTrue()
    {
        ToolCallDisplayFormatter.IsLongValue(new string('x', 401))
            .Should().BeTrue();
    }

    [Fact]
    public void IsLongValue_Over8Lines_ShouldBeTrue()
    {
        var value = string.Join("\n", Enumerable.Range(1, 9).Select(i => $"line{i}"));
        ToolCallDisplayFormatter.IsLongValue(value).Should().BeTrue();
    }

    [Fact]
    public void FormatJsonPretty_ValidObject_ShouldIndent()
    {
        var pretty = ToolCallDisplayFormatter.FormatJsonPretty("""{"a":1}""");
        pretty.Should().Contain("\n");
        pretty.Should().Contain("\"a\"");
    }

    [Fact]
    public void FormatJsonPretty_Invalid_ShouldReturnOriginal()
    {
        ToolCallDisplayFormatter.FormatJsonPretty("plain")
            .Should().Be("plain");
    }

    [Fact]
    public void FormatReadableContent_Json_ShouldPrettyPrint()
    {
        var readable = ToolCallDisplayFormatter.FormatReadableContent("""{"ok":true}""");
        readable.Should().Contain("\n");
    }

    [Fact]
    public void FormatReadableContent_PlainText_ShouldReturnAsIs()
    {
        ToolCallDisplayFormatter.FormatReadableContent("新文件已创建。")
            .Should().Be("新文件已创建。");
    }

    [Fact]
    public void FormatKeyValueCopyText_ShouldJoinLines()
    {
        var entries = new[]
        {
            new ToolCallParameterEntry("filePath", @"E:\a.cs", false),
            new ToolCallParameterEntry("content", "hi", false)
        };

        ToolCallDisplayFormatter.FormatKeyValueCopyText(entries)
            .Should().Be("filePath: E:\\a.cs\ncontent: hi");
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run:

```bash
dotnet test tests/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj --filter "FullyQualifiedName~ToolCallDisplayFormatterTests" -v q
```

Expected: FAIL（类型/命名空间不存在）

- [ ] **Step 3: 实现 formatter**

Create `samples/Seeing.Agent.WebUI/Helpers/ToolCallDisplayFormatter.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Helpers;

/// <summary>
/// 工具调用参数/结果展示格式化（供 ToolCallMessageComponent 使用）
/// </summary>
public sealed record ToolCallParameterEntry(string Name, string Value, bool IsLong);

public static class ToolCallDisplayFormatter
{
    private const int LongValueCharThreshold = 400;
    private const int LongValueLineThreshold = 8;

    private static readonly JsonSerializerOptions s_prettyOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions s_compactOptions = new()
    {
        WriteIndented = false
    };

    public static bool TryParseParameters(string? parametersJson, out IReadOnlyList<ToolCallParameterEntry> entries)
    {
        entries = Array.Empty<ToolCallParameterEntry>();
        if (string.IsNullOrWhiteSpace(parametersJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var list = new List<ToolCallParameterEntry>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = FormatPropertyValue(prop.Value);
                list.Add(new ToolCallParameterEntry(prop.Name, value, IsLongValue(value)));
            }

            if (list.Count == 0)
                return false;

            entries = list;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsLongValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (value.Length > LongValueCharThreshold)
            return true;

        var lines = 1;
        foreach (var ch in value)
        {
            if (ch == '\n')
            {
                lines++;
                if (lines > LongValueLineThreshold)
                    return true;
            }
        }

        return false;
    }

    public static string FormatJsonPretty(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, s_prettyOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static string FormatReadableContent(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                return JsonSerializer.Serialize(doc.RootElement, s_prettyOptions);
            }
            catch (JsonException)
            {
                // fall through
            }
        }

        return content;
    }

    public static string FormatKeyValueCopyText(IEnumerable<ToolCallParameterEntry> entries)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var entry in entries)
        {
            if (!first)
                sb.Append('\n');
            first = false;
            sb.Append(entry.Name).Append(": ").Append(entry.Value);
        }

        return sb.ToString();
    }

    private static string FormatPropertyValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Object or JsonValueKind.Array =>
            JsonSerializer.Serialize(element, s_compactOptions),
        _ => element.GetRawText()
    };
}
```

- [ ] **Step 4: 运行测试确认通过**

Run:

```bash
dotnet test tests/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj --filter "FullyQualifiedName~ToolCallDisplayFormatterTests" -v q
```

Expected: PASS（全部测试通过）

- [ ] **Step 5: Commit** — **跳过**（用户要求不 commit）

---

### Task 2: 展开区 CSS

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/tool-components.css`

**Interfaces:**
- Consumes: 现有 CSS 变量（`--color-*`、`--space-*`、`--radius-*`、`--font-size-*`）
- Produces: 下列 class，供 Task 3 使用

- [ ] **Step 1: 在 `tool-components.css` 文件末尾追加样式**

在文件末尾（`.tool-call-block:hover` 暗色规则之后）追加：

```css
/* ========== ToolCall 展开详情 ========== */
.tool-call-details {
    padding: var(--space-2) var(--space-3);
    background: var(--color-bg-tertiary);
    border-radius: var(--radius-sm);
    margin-top: 2px;
    border: 1px solid var(--color-border-secondary);
}

.tool-call-section {
    margin-bottom: var(--space-3);
}

.tool-call-section:last-child {
    margin-bottom: 0;
}

.tool-call-section-header {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    margin-bottom: var(--space-1);
    flex-wrap: wrap;
}

.tool-call-section-title {
    font-size: var(--font-size-xs);
    color: var(--color-text-secondary);
    font-weight: 500;
}

.tool-call-section-title.is-error {
    color: var(--color-error);
}

.tool-call-section-title.is-success {
    color: var(--color-success);
}

.tool-call-view-toggle {
    display: inline-flex;
    gap: 2px;
    background: var(--color-bg-secondary);
    border-radius: var(--radius-sm);
    padding: 1px;
}

.tool-call-view-toggle button {
    border: none;
    background: transparent;
    color: var(--color-text-secondary);
    font-size: 11px;
    padding: 2px 8px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    line-height: 1.4;
}

.tool-call-view-toggle button.active {
    background: var(--color-bg);
    color: var(--color-text);
    box-shadow: var(--shadow-sm);
}

.tool-call-section-actions {
    margin-left: auto;
    display: inline-flex;
    align-items: center;
    gap: var(--space-1);
}

.tool-call-kv-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 12px;
}

.tool-call-kv-table th,
.tool-call-kv-table td {
    text-align: left;
    vertical-align: top;
    padding: 4px 6px;
    border-bottom: 1px solid var(--color-border-secondary);
}

.tool-call-kv-table th {
    width: 120px;
    max-width: 30%;
    color: var(--color-text-secondary);
    font-weight: 500;
    word-break: break-all;
}

.tool-call-kv-table td {
    color: var(--color-text);
}

.tool-call-kv-value {
    margin: 0;
    white-space: pre-wrap;
    word-break: break-word;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    font-size: 12px;
}

.tool-call-kv-value.is-long {
    max-height: 160px;
    overflow: auto;
    padding: 4px 6px;
    background: var(--color-bg-secondary);
    border-radius: var(--radius-sm);
}

.tool-call-pre {
    margin: 0;
    padding: 8px;
    background: var(--color-bg-secondary);
    border-radius: var(--radius-sm);
    font-size: 12px;
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
    white-space: pre-wrap;
    word-break: break-word;
    overflow: auto;
}

.tool-call-pre.is-collapsed {
    max-height: 240px;
}

.tool-call-pre.is-error {
    color: var(--color-error);
    background: var(--color-error-bg);
    border: 1px solid rgba(255, 77, 79, 0.2);
}

[data-theme="dark"] .tool-call-details {
    background: rgba(255, 255, 255, 0.04);
    border-color: rgba(255, 255, 255, 0.1);
}

[data-theme="dark"] .tool-call-view-toggle {
    background: rgba(255, 255, 255, 0.06);
}

[data-theme="dark"] .tool-call-view-toggle button.active {
    background: rgba(255, 255, 255, 0.12);
}

[data-theme="dark"] .tool-call-kv-value.is-long,
[data-theme="dark"] .tool-call-pre {
    background: rgba(0, 0, 0, 0.25);
}
```

- [ ] **Step 2: 确认 CSS 已挂载**

检查 `samples/Seeing.Agent.WebUI/Components/App.razor`（或 `_Host` / `index.html` / layout）是否已引用 `css/tool-components.css`。若已引用则无需改动；若未引用，在现有 stylesheet 列表中增加：

```html
<link rel="stylesheet" href="css/tool-components.css" />
```

（以项目现有引用方式为准，路径与其它 css 一致。）

- [ ] **Step 3: Commit** — **跳过**

---

### Task 3: 重写 ToolCallMessageComponent 展开区

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/Messaging/ToolCallMessageComponent.razor`

**Interfaces:**
- Consumes: `ToolCallDisplayFormatter`、`ToolCallParameterEntry`、`IJSRuntime`、`MessageService`（可选，与 AgentMdPreview 一致）
- Produces: 展开区交互（参数键值表/JSON、结果可读/原始、展开全部、复制）

- [ ] **Step 1: 替换整个组件内容**

将 `ToolCallMessageComponent.razor` 替换为：

```razor
@using AntDesign
@using Seeing.Agent.WebUI.Helpers
@using Seeing.Agent.WebUI.Models
@using Seeing.Agent.WebUI.Rendering.Abstractions
@inject IJSRuntime JSRuntime
@inject MessageService MessageService

@* 工具调用消息组件 - 渲染工具调用信息 *@

@if (ToolCall.IsTodoTool && ToolCall.TodoList != null && ToolCall.TodoList.HasTasks)
{
    <TodoCard TodoList="@ToolCall.TodoList" />
}
else
{
    <div class="tool-call-block" @onclick="ToggleExpand">
        <Icon Type="@ToolCall.StatusIcon" Style="@ToolCall.StatusIconStyle" />
        <span style="margin-left: 6px; font-weight: 500; font-size: 13px;">@ToolCall.Name</span>
        <Tag Color="@ToolCall.StatusTagColor" Style="margin-left: 6px; font-size: 11px;">@ToolCall.StatusText</Tag>

        @if (!string.IsNullOrEmpty(ToolCall.Result))
        {
            <span style="margin-left: 8px; color: var(--color-success); font-size: 12px; max-width: 200px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;">
                @GetResultPreview()
            </span>
        }
        else if (!string.IsNullOrEmpty(ToolCall.Error))
        {
            <span style="margin-left: 8px; color: var(--color-error); font-size: 12px; max-width: 200px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;">
                @GetErrorPreview()
            </span>
        }

        @if (ToolCall.IsRunning)
        {
            <Spin Size="@SpinSize.Small" Style="margin-left: 8px;" />
        }

        <Icon Type="@(ToolCall.IsExpanded ? IconType.Outline.Down : IconType.Outline.Right)"
              Style="margin-left: auto; font-size: 12px; color: var(--color-text-tertiary);" />
    </div>

    @if (ToolCall.IsExpanded)
    {
        <div class="tool-call-details" @onclick:stopPropagation="true">
            @if (HasParametersSection)
            {
                <div class="tool-call-section">
                    <div class="tool-call-section-header">
                        <span class="tool-call-section-title">参数</span>
                        <div class="tool-call-view-toggle">
                            <button type="button" class="@(_paramView == ParamView.KeyValue ? "active" : null)"
                                    @onclick="@(() => _paramView = ParamView.KeyValue)">键值表</button>
                            <button type="button" class="@(_paramView == ParamView.Json ? "active" : null)"
                                    @onclick="@(() => _paramView = ParamView.Json)">JSON</button>
                        </div>
                        <div class="tool-call-section-actions">
                            <Button Size="@ButtonSize.Small" Type="@ButtonType.Text"
                                    OnClick="CopyParametersAsync">复制</Button>
                        </div>
                    </div>

                    @if (_paramView == ParamView.KeyValue && _paramEntries.Count > 0)
                    {
                        <table class="tool-call-kv-table">
                            <tbody>
                            @foreach (var entry in _paramEntries)
                            {
                                <tr>
                                    <th>@entry.Name</th>
                                    <td>
                                        <pre class="tool-call-kv-value @(entry.IsLong ? "is-long" : null)">@entry.Value</pre>
                                    </td>
                                </tr>
                            }
                            </tbody>
                        </table>
                    }
                    else
                    {
                        <pre class="tool-call-pre">@_paramJsonDisplay</pre>
                    }
                </div>
            }

            @if (!string.IsNullOrEmpty(ToolCall.Result) && !ToolCall.IsTodoTool)
            {
                <div class="tool-call-section">
                    <div class="tool-call-section-header">
                        <span class="tool-call-section-title is-success">结果</span>
                        <div class="tool-call-view-toggle">
                            <button type="button" class="@(_resultView == ResultView.Readable ? "active" : null)"
                                    @onclick="@(() => _resultView = ResultView.Readable)">可读</button>
                            <button type="button" class="@(_resultView == ResultView.Raw ? "active" : null)"
                                    @onclick="@(() => _resultView = ResultView.Raw)">原始</button>
                        </div>
                        <div class="tool-call-section-actions">
                            <Button Size="@ButtonSize.Small" Type="@ButtonType.Text"
                                    OnClick="@(() => _resultExpanded = !_resultExpanded)">
                                @(_resultExpanded ? "收起" : "展开全部")
                            </Button>
                            <Button Size="@ButtonSize.Small" Type="@ButtonType.Text"
                                    OnClick="CopyResultAsync">复制</Button>
                        </div>
                    </div>
                    <pre class="tool-call-pre @(_resultExpanded ? null : "is-collapsed")">@GetResultDisplayText()</pre>
                </div>
            }

            @if (!string.IsNullOrEmpty(ToolCall.Error))
            {
                <div class="tool-call-section">
                    <div class="tool-call-section-header">
                        <span class="tool-call-section-title is-error">错误</span>
                        <div class="tool-call-view-toggle">
                            <button type="button" class="@(_errorView == ResultView.Readable ? "active" : null)"
                                    @onclick="@(() => _errorView = ResultView.Readable)">可读</button>
                            <button type="button" class="@(_errorView == ResultView.Raw ? "active" : null)"
                                    @onclick="@(() => _errorView = ResultView.Raw)">原始</button>
                        </div>
                        <div class="tool-call-section-actions">
                            <Button Size="@ButtonSize.Small" Type="@ButtonType.Text"
                                    OnClick="@(() => _errorExpanded = !_errorExpanded)">
                                @(_errorExpanded ? "收起" : "展开全部")
                            </Button>
                            <Button Size="@ButtonSize.Small" Type="@ButtonType.Text"
                                    OnClick="CopyErrorAsync">复制</Button>
                        </div>
                    </div>
                    <pre class="tool-call-pre is-error @(_errorExpanded ? null : "is-collapsed")">@GetErrorDisplayText()</pre>
                </div>
            }
        </div>
    }
}

@code {
    private enum ParamView { KeyValue, Json }
    private enum ResultView { Readable, Raw }

    [Parameter]
    public ToolCallViewModel ToolCall { get; set; } = new();

    [Parameter]
    public ContentBlock Block { get; set; } = default!;

    [Parameter]
    public RenderContext Context { get; set; } = default!;

    [Parameter]
    public EventCallback<ToolCallViewModel> OnToolClick { get; set; }

    private ParamView _paramView = ParamView.KeyValue;
    private ResultView _resultView = ResultView.Readable;
    private ResultView _errorView = ResultView.Readable;
    private bool _resultExpanded;
    private bool _errorExpanded;
    private IReadOnlyList<ToolCallParameterEntry> _paramEntries = Array.Empty<ToolCallParameterEntry>();
    private string _paramJsonDisplay = "";
    private bool _hasParsedParams;
    private string? _lastParametersSource;

    private bool HasParametersSection =>
        !ToolCall.IsTodoTool
        && !string.IsNullOrEmpty(ToolCall.Parameters)
        && ToolCall.Parameters != "{}"
        && (_hasParsedParams || !string.IsNullOrWhiteSpace(ToolCall.Parameters));

    protected override void OnParametersSet()
    {
        if (_lastParametersSource == ToolCall.Parameters)
            return;

        _lastParametersSource = ToolCall.Parameters;
        _hasParsedParams = ToolCallDisplayFormatter.TryParseParameters(ToolCall.Parameters, out _paramEntries);
        _paramJsonDisplay = ToolCallDisplayFormatter.FormatJsonPretty(ToolCall.Parameters);
        if (!_hasParsedParams)
            _paramView = ParamView.Json;
    }

    private void ToggleExpand()
    {
        ToolCall.IsExpanded = !ToolCall.IsExpanded;
    }

    private string GetResultPreview()
    {
        if (string.IsNullOrEmpty(ToolCall.Result)) return "";
        var firstLine = ToolCall.Result.Split('\n')[0].Trim();
        if (firstLine.StartsWith("{") || firstLine.StartsWith("["))
        {
            try
            {
                var element = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(ToolCall.Result);
                if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    var firstProp = element.EnumerateObject().FirstOrDefault();
                    if (firstProp.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        return firstProp.Value.GetString() ?? firstLine[..Math.Min(40, firstLine.Length)];
                }
            }
            catch
            {
                // ignore
            }
        }
        if (firstLine.Length > 40) return firstLine[..40] + "...";
        return firstLine;
    }

    private string GetErrorPreview()
    {
        if (string.IsNullOrEmpty(ToolCall.Error)) return "";
        if (ToolCall.Error.Length > 40) return ToolCall.Error[..40] + "...";
        return ToolCall.Error;
    }

    private string GetResultDisplayText() =>
        _resultView == ResultView.Raw
            ? ToolCall.Result ?? ""
            : ToolCallDisplayFormatter.FormatReadableContent(ToolCall.Result);

    private string GetErrorDisplayText() =>
        _errorView == ResultView.Raw
            ? ToolCall.Error ?? ""
            : ToolCallDisplayFormatter.FormatReadableContent(ToolCall.Error);

    private async Task CopyParametersAsync()
    {
        var text = _paramView == ParamView.KeyValue && _paramEntries.Count > 0
            ? ToolCallDisplayFormatter.FormatKeyValueCopyText(_paramEntries)
            : _paramJsonDisplay;
        await CopyTextAsync(text);
    }

    private Task CopyResultAsync() => CopyTextAsync(GetResultDisplayText());

    private Task CopyErrorAsync() => CopyTextAsync(GetErrorDisplayText());

    private async Task CopyTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            await JSRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text);
            MessageService.Success("已复制到剪贴板");
        }
        catch
        {
            // 剪贴板不可用时静默失败
        }
    }
}
```

注意：

- 删除旧的 `FormatParametersBrief` / `TruncateResult`
- 点击展开区内部控件时用 `@onclick:stopPropagation` 避免误折叠
- 参数解析失败时自动切到 JSON 视图并显示原始/美化失败后的原文

- [ ] **Step 2: 构建 WebUI 确认无编译错误**

Run:

```bash
dotnet build samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj -v q
```

Expected: Build succeeded，0 Error

- [ ] **Step 3: 再跑 formatter 测试**

Run:

```bash
dotnet test tests/Seeing.Agent.WebUI.Tests/Seeing.Agent.WebUI.Tests.csproj --filter "FullyQualifiedName~ToolCallDisplayFormatterTests" -v q
```

Expected: PASS

- [ ] **Step 4: Commit** — **跳过**

---

### Task 4: 手动验收清单

**Files:** 无代码改动

- [ ] **Step 1: 运行 WebUI**

```bash
dotnet run --project samples/Seeing.Agent.WebUI
```

- [ ] **Step 2: 按清单核对**

| # | 场景 | 期望 |
|---|------|------|
| 1 | 触发写文件工具并展开 | 参数键值表显示真实 `filePath` 与 `content`，不再是 `filePath, content` |
| 2 | 切换参数「JSON」 | 看到格式化 JSON，含完整值 |
| 3 | 点参数「复制」 | 剪贴板有当前视图文本，提示「已复制到剪贴板」 |
| 4 | 结果区 | 完整结果可滚动；默认 max-height 约 240px |
| 5 | 「展开全部」 | 取消高度限制；再点变为「收起」 |
| 6 | 结果「原始」 | 显示未美化原文 |
| 7 | 工具失败 | 错误区可读/原始/展开/复制可用，错误色 |
| 8 | `todowrite` | 仍只显示 TodoCard，无通用参数/结果区 |
| 9 | 收起行 | 仍为工具名+状态+短预览 |
| 10 | 暗色主题 | 展开区对比度可读 |

- [ ] **Step 3: Commit** — **跳过**

---

## Spec Coverage Check

| Spec 要求 | Task |
|-----------|------|
| 参数键值表显示真实值 | Task 1 + Task 3 |
| 参数可切 JSON | Task 3 |
| 结果可读/原始 + 展开全部 | Task 3 |
| 复制 | Task 3 |
| 收起行不变 | Task 3（保留原 header） |
| todowrite 特殊处理 | Task 3 |
| 空参数不渲染分区 | Task 1 (`{}` → false) + Task 3 `HasParametersSection` |
| 长值滚动不截断 | Task 1 `IsLong` + Task 2/3 CSS |
| 不改数据管道 | 全计划未改 Session/Event |
| CSS 亮/暗色 | Task 2 |

## Placeholder / Consistency Self-Review

- 无 TBD/TODO
- API 名称统一：`TryParseParameters`、`FormatJsonPretty`、`FormatReadableContent`、`FormatKeyValueCopyText`、`IsLongValue`、`ToolCallParameterEntry`
- Commit 步骤按用户要求跳过
