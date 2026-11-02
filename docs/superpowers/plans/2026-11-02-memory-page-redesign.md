# 记忆页面重设计 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `/memory` 从「空白搜索页」改成「概览 + 类型/时间分布 + 完整列表」的单页总览，修掉让时间分布失真的 `created_at` 解析 bug，并把记忆配置归入设置页签。

**Architecture:** 后端加一个 `GetOverviewAsync` 聚合（一次读盘出全部统计），修 `LocalFileStore.ParseMetadata` 的键名错配；前端重写 `MemoryPage.razor`，删掉 `browse`/`stats`/`settings` 三个页面，设置内容改走既有的 `SettingsCardContribution` 通道。

**Tech Stack:** .NET 10、C# 13、xUnit 2.9 + FluentAssertions 6.12 + Moq 4.20、Blazor Server、AntDesign 1.6.2

**Spec:** `docs/superpowers/specs/2026-11-02-memory-page-redesign-design.md`

## Global Constraints

- 注释与用户可见文案**全部中文**。
- 测试一律用 VSTest 跑已构建程序集，**不用** `dotnet test`（MTP 在本环境无法发现测试）：
  `dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll`
- 每次跑测试**必须确认输出里有「总计: N」且 N > 0**。过滤器拼错会 0 用例执行且 exit 0，伪造全绿。
- 变异验证**必须加 `--no-incremental`**。`Copy-Item` 还原会保留旧时间戳，MSBuild 增量复用旧产物会得出假结论。
- 改 C#/razor 源码**只用 `edit` / `write` 工具**。PowerShell `-replace` 会破坏 UTF-8 编码。
- PowerShell 判定测试结果只能用 `$LASTEXITCODE` + 落盘读取；用 `Out-String` + 中文 `-match` 永不命中。
- Razor 陷阱：列表用原生 `<table class="items-table">`，**不用** `<Table>/<PropertyColumn>`；
  `Size="@ButtonSize.Small"`；`Tag Color="@("blue")"`。
- 能力包**不得**引用 Core 具体类型或 WebUI 类型。
- 不要动 `Core/MemoryType.cs`（`Semantic/Episodic/...`，全仓无消费者的死枚举）。

## Review Focus

以下五类是设计上未写死、但使用者一定会撞上的行为，各自在所属任务里有测试或验收项：

1. **无 `created_at` 的文件**（`session/{id}/index.md` 手写头）：不能让时间分布把它算成「今天」，否则空档图被永久污染。
2. **`days` 窗口边界**：今天当天的记忆必须落在最后一根柱子上；恰好 `days` 天前那条必须**不**在窗口内。
3. **空知识库**：`TotalCount == 0` 时页面必须给出可行动的空状态，而不是空白。
4. **`CreatedAt` 与 `ModifiedTime` 冲突**（老记忆被编辑过）：时间分布按创建时间，列表排序按修改时间，两者不得混用。
5. **仅 `session` 类型有数据**：`CountByType` 的 `Daily`/`Digest` 必须是 `0` 而不是缺失，前端卡片才能稳定渲染四张。

---

## Task 1: 修 `created_at` 解析与 `source_session`

**Files:**
- Modify: `src/capabilities/Seeing.Agent.Memory/Core/Storage/LocalFileStore.cs:418-470`（`CreateFileNode` / `ParseMetadata`）
- Modify: `src/capabilities/Seeing.Agent.Memory/Core/Storage/LocalFileStore.cs:530-551`（`GetDateTime`）
- Modify: `src/capabilities/Seeing.Agent.Memory/Core/Models/FileMetadata.cs`
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Storage/LocalFileStoreCreatedAtTests.cs`（新建）

**Interfaces:**
- Consumes: 无
- Produces: `FileMetadata.SourceSession`（`string?`，默认 `null`）
- Produces: `ParseMetadata` 的新签名 `private FileMetadata ParseMetadata(Dictionary<string, object?> frontMatter, string path, DateTimeOffset modifiedTime)`

- [ ] **Step 1: 写失败的测试**

新建 `tests/capabilities/Seeing.Agent.Memory.Tests/Storage/LocalFileStoreCreatedAtTests.cs`。
沿用 `LocalFileStoreTests` 的临时目录模式（`new LocalFileStore(_testDirectory)`）。

```csharp
using FluentAssertions;
using Seeing.Agent.Memory.Core.Models;
using Seeing.Agent.Memory.Core.Storage;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Storage;

/// <summary>
/// created_at 键名解析。写入点统一写 snake_case（created_at），
/// 历史上解析器只读 camelCase（createdAt），导致每个文件的 CreatedAt 都回落 UtcNow。
/// 本组用例是那次错配的回归锚点。
/// </summary>
public class LocalFileStoreCreatedAtTests : IDisposable
{
    private readonly string _dir;
    private readonly LocalFileStore _store;

    public LocalFileStoreCreatedAtTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Seeing.Agent.Memory.Tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "daily"));
        _store = new LocalFileStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteFile(string relPath, string content)
    {
        var full = Path.Combine(_dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public async Task ReadAsync_With_SnakeCase_CreatedAt_Should_Use_Written_Value()
    {
        // 回归锚点：修之前这条必红（读出来是 UtcNow）。
        WriteFile("daily/2026-01-02/a.md", """
            ---
            id: a
            type: daily
            title: "旧记忆"
            created_at: 2026-01-02T03:04:05+00:00
            ---

            正文
            """);

        var node = await _store.ReadAsync("daily/2026-01-02/a.md");

        node!.Metadata.CreatedAt.Should().Be(
            DateTimeOffset.Parse("2026-01-02T03:04:05+00:00"),
            "写入点写的是 created_at，解析器必须读它");
    }

    [Fact]
    public async Task ReadAsync_With_CamelCase_CreatedAt_Should_Still_Work()
    {
        WriteFile("daily/2026-01-02/b.md", """
            ---
            id: b
            type: daily
            createdAt: 2026-02-03T04:05:06+00:00
            ---

            正文
            """);

        var node = await _store.ReadAsync("daily/2026-01-02/b.md");

        node!.Metadata.CreatedAt.Should().Be(DateTimeOffset.Parse("2026-02-03T04:05:06+00:00"));
    }

    [Fact]
    public async Task ReadAsync_Without_CreatedAt_Should_FallBack_To_FileMtime_Not_UtcNow()
    {
        // session/{id}/index.md 由 AppendSessionIndexAsync 手写头，只有 type + title。
        // 回落 UtcNow 会让它每次都算「今天」，时间分布被永久污染。
        var full = WriteFile("session/s1/index.md", """
            ---
            type: session
            title: session-index
            ---

            - 12:00:00 [fact] 某事 → [[daily/2026-01-02/x.md]]
            """);

        var expected = new DateTimeOffset(new FileInfo(full).LastWriteTimeUtc, TimeSpan.Zero);

        var node = await _store.ReadAsync("session/s1/index.md");

        node!.Metadata.CreatedAt.Should().BeCloseTo(expected, TimeSpan.FromSeconds(1),
            "缺 created_at 时回落文件 mtime，而不是 UtcNow");
        node.Metadata.CreatedAt.Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(-1),
            "刚写完的文件若被赋值 UtcNow，这条会在边界上失败；回落 mtime 才是正确行为");
    }

    [Fact]
    public async Task ReadAsync_Should_Parse_SourceSession_From_FrontMatter()
    {
        WriteFile("daily/2026-01-02/c.md", """
            ---
            id: c
            type: daily
            source_session: sess-abc123
            created_at: 2026-01-02T03:04:05+00:00
            ---

            正文
            """);

        var node = await _store.ReadAsync("daily/2026-01-02/c.md");

        node!.Metadata.SourceSession.Should().Be("sess-abc123");
    }

    [Fact]
    public async Task ReadAsync_Should_Infer_SourceSession_From_SessionIndexPath()
    {
        WriteFile("session/sess-xyz789/index.md", """
            ---
            type: session
            title: session-index
            ---

            正文
            """);

        var node = await _store.ReadAsync("session/sess-xyz789/index.md");

        node!.Metadata.SourceSession.Should().Be("sess-xyz789",
            "session/{id}/index.md 的 id 就是来源会话，路径约定应属存储层");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Memory.Tests -v q --nologo
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll --TestCaseFilter:"FullyQualifiedName~LocalFileStoreCreatedAtTests" > "$env:TEMP\t1.txt" 2>&1
"exit=$LASTEXITCODE"
Get-Content "$env:TEMP\t1.txt" -Encoding UTF8 | Where-Object { $_ -match 'net10\.0\)' } | Select-Object -Last 1
```

预期：编译失败（`SourceSession` 不存在）。这是本任务的起点，说明测试确实覆盖了新接口。

- [ ] **Step 3: 改 `FileMetadata` 加 `SourceSession`**

```csharp
public record FileMetadata(
    string Id,
    MemoryType Type,
    string? Title,
    IReadOnlyList<string> Tags,
    double Importance,
    double Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? SourceSession = null
);
```

默认值放参数末尾，既有构造调用（`FileMetadata.Create`、测试）无需改动。
`FileMetadata.Create` 保持不传（默认 `null`）。

- [ ] **Step 4: 改 `CreateFileNode` 把 mtime 传进 `ParseMetadata`**

`CreateFileNode` 里已算出 `var modifiedTime = fileInfo.LastWriteTimeUtc;`，把调用改为：

```csharp
var metadata = ParseMetadata(frontMatter, relativePath.Replace('\\', '/'), modifiedTime);
```

（`ParseMetadata` 内部的 `InferType(path)` 需要 `/` 分隔的路径才能匹配 `session/` 前缀，所以先归一化。）

- [ ] **Step 5: 改 `ParseMetadata` 签名与实现**

```csharp
private FileMetadata ParseMetadata(
    Dictionary<string, object?> frontMatter,
    string path,
    DateTimeOffset modifiedTime)
{
    var id = GetString(frontMatter, "id") ?? Guid.NewGuid().ToString("N")[..8];
    var typeStr = GetString(frontMatter, "type");
    var title = GetString(frontMatter, "title");
    var tags = GetStringList(frontMatter, "tags") ?? Array.Empty<string>();
    var importance = GetDouble(frontMatter, "importance") ?? 0.5;
    var confidence = GetDouble(frontMatter, "confidence") ?? 1.0;

    // 写入点（MemoryPipeline / MemoryWriteTool / LlmMemoryEvolution）统一写 snake_case，
    // 历史与手工文件可能是 camelCase。两者都认，避免每个文件都回落成「当前时刻」。
    var createdAt = GetDateTime(frontMatter, "created_at")
                 ?? GetDateTime(frontMatter, "createdAt")
                 ?? modifiedTime;   // 无创建时间（如 session/{id}/index.md 手写头）→ 回落文件 mtime

    var expiresAt = GetDateTime(frontMatter, "expiresAt");

    var type = InferType(path);
    if (typeStr != null && Enum.TryParse<ReMeMemoryType>(typeStr, ignoreCase: true, out var parsedType))
        type = parsedType;

    // session/{id}/index.md 没有 source_session 字段，但路径里的 id 就是来源会话。
    var sourceSession = GetString(frontMatter, "source_session")
                     ?? InferSourceSessionFromPath(path);

    return new FileMetadata(
        Id: id,
        Type: type,
        Title: title,
        Tags: tags,
        Importance: importance,
        Confidence: confidence,
        CreatedAt: createdAt,
        ExpiresAt: expiresAt,
        SourceSession: sourceSession);
}

/// <summary>从 session/{sessionId}/index.md 推断来源会话；其它路径返回 null。</summary>
private static string? InferSourceSessionFromPath(string path)
{
    const string prefix = "session/";
    if (!path.StartsWith(prefix, StringComparison.Ordinal))
        return null;

    var rest = path[prefix.Length..];
    var slash = rest.IndexOf('/');
    if (slash <= 0)
        return null;

    var sessionId = rest[..slash];
    return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
}
```

- [ ] **Step 6: 修 `GetDateTime` 的判定顺序**

当前 `long.TryParse` 在 `DateTimeOffset.TryParse` **之前**，8 位纯数字文本（`20260101`）
会被当成 unix 毫秒（= 1970-08-23），而它更可能是一个日期。

```csharp
private static DateTimeOffset? GetDateTime(Dictionary<string, object?> dict, string key)
{
    if (!dict.TryGetValue(key, out var value) || value == null)
        return null;

    if (value is DateTimeOffset dto)
        return dto;

    if (value is DateTime dt)
        return new DateTimeOffset(dt, TimeSpan.Zero);

    if (value is long ms)
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);

    var text = value.ToString();
    if (string.IsNullOrWhiteSpace(text))
        return null;

    // 先按日期文本解析：8 位纯数字（20260101）既能被 long.TryParse 吃下，
    // 又更像日期；当成 unix 毫秒会得到 1970 年。
    if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDto))
        return parsedDto;

    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMs))
        return DateTimeOffset.FromUnixTimeMilliseconds(parsedMs);

    return null;
}
```

需要 `using System.Globalization;`（检查文件头是否已有）。

**注意**：`DateTimeOffset.TryParse("20260101")` 会成功解析为 `2026-01-01`。
这是本次想要的行为。写入点产生的 `2026-01-02T03:04:05.1234567+00:00` 走第一条分支不受影响。

- [ ] **Step 7: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Memory.Tests -v q --nologo
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll > "$env:TEMP\t1b.txt" 2>&1
"exit=$LASTEXITCODE"
Get-Content "$env:TEMP\t1b.txt" -Encoding UTF8 | Where-Object { $_ -match 'net10\.0\)' } | Select-Object -Last 1
```

预期：全绿，**总计 > 0**。确认 5 条新用例都在里面（总数应比改动前多 5）。

- [ ] **Step 8: 变异验证**

把 `created_at` 那一行改回只读 `createdAt`（临时），**加 `--no-incremental` 重建**，确认
`ReadAsync_With_SnakeCase_CreatedAt_Should_Use_Written_Value` 变红。
再还原。同理验证：把 `?? modifiedTime` 改成 `?? DateTimeOffset.UtcNow`，
`ReadAsync_Without_CreatedAt_Should_FallBack_To_FileMtime_Not_UtcNow` 必须变红。

```powershell
dotnet build tests/capabilities/Seeing.Agent.Memory.Tests --no-incremental -v q --nologo
```

- [ ] **Step 9: 提交**

```bash
git add src/capabilities/Seeing.Agent.Memory/Core/Storage/LocalFileStore.cs src/capabilities/Seeing.Agent.Memory/Core/Models/FileMetadata.cs tests/capabilities/Seeing.Agent.Memory.Tests/Storage/LocalFileStoreCreatedAtTests.cs
git commit -m "fix(memory): 解析 created_at 并补 source_session，时间分布不再回落此刻"
```

---

## Task 2: 新增 `GetOverviewAsync` 聚合

**Files:**
- Create: `src/capabilities/Seeing.Agent.Memory/Abstractions/MemoryOverview.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Abstractions/IMemoryService.cs`
- Modify: `src/capabilities/Seeing.Agent.Memory/Core/MemoryService.cs`
- Test: `tests/capabilities/Seeing.Agent.Memory.Tests/Core/MemoryOverviewTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 `FileMetadata.CreatedAt`（真实创建时间）
- Produces:
  - `record MemoryDailyCount(DateOnly Date, int Count)`
  - `record MemoryOverview(int TotalCount, IReadOnlyDictionary<MemoryType,int> CountByType, IReadOnlyList<MemoryDailyCount> DailyCounts, IndexStats Index, GraphStats Graph)`
  - `Task<MemoryOverview> IMemoryService.GetOverviewAsync(int days = 14, CancellationToken ct = default)`

- [ ] **Step 1: 写失败的测试**

新建 `tests/capabilities/Seeing.Agent.Memory.Tests/Core/MemoryOverviewTests.cs`。
用 Moq 造 `IFileStore` / `IMemoryIndex` / `IMemoryGraph`，直接调 `new MemoryService(store.Object, index.Object, graph.Object)`。

```csharp
using FluentAssertions;
using Moq;
using Seeing.Agent.Memory.Abstractions;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Core;

/// <summary>总览聚合：类型分布恒三键、时间分布恒 N 项、窗口内外语义分离。</summary>
public class MemoryOverviewTests
{
    private static FileNode Node(string path, MemoryType type, DateTimeOffset createdAt)
        => FileNode.Create(path, "正文", FileMetadata.Create("id", type, "t") with { CreatedAt = createdAt });

    private static MemoryService Build(IReadOnlyList<FileNode> files)
    {
        var store = new Mock<IFileStore>();
        store.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(files);

        var index = new Mock<IMemoryIndex>();
        index.Setup(i => i.GetStatsAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new IndexStats(0, 0, 0, DateTimeOffset.UtcNow));

        var graph = new Mock<IMemoryGraph>();
        graph.Setup(g => g.GetStatsAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(new GraphStats(0, 0, 0));

        return new MemoryService(store.Object, index.Object, graph.Object);
    }

    [Fact]
    public async Task GetOverview_CountByType_Should_Include_Zero_Value_Types()
    {
        // 只有 session 数据，daily/digest 也必须在字典里且为 0，前端四张卡片才稳定。
        var overview = await Build([Node("session/s1/index.md", MemoryType.Session, DateTimeOffset.Now)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.CountByType.Should().HaveCount(3);
        overview.CountByType[MemoryType.Session].Should().Be(1);
        overview.CountByType[MemoryType.Daily].Should().Be(0);
        overview.CountByType[MemoryType.Digest].Should().Be(0);
    }

    [Fact]
    public async Task GetOverview_DailyCounts_Should_Have_Exactly_N_Items()
    {
        var overview = await Build([]).GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.DailyCounts.Should().HaveCount(14, "空库也要给满 14 个槽，前端不必补零");
        overview.DailyCounts.Should().OnlyContain(d => d.Count == 0);
    }

    [Fact]
    public async Task GetOverview_DailyCounts_Should_Place_Today_In_Last_Slot()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var overview = await Build([Node("daily/t/a.md", MemoryType.Daily, DateTimeOffset.Now)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.DailyCounts[^1].Date.Should().Be(today, "最后一根柱子是今天");
        overview.DailyCounts[^1].Count.Should().Be(1);
        overview.DailyCounts[0].Date.Should().Be(today.AddDays(-13));
    }

    [Fact]
    public async Task GetOverview_Should_Exclude_Entries_Outside_Window_From_Daily_But_Keep_In_Total()
    {
        var old = DateTimeOffset.Now.AddDays(-30);
        var overview = await Build([Node("daily/old/a.md", MemoryType.Daily, old)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.TotalCount.Should().Be(1, "总数不受窗口限制");
        overview.CountByType[MemoryType.Daily].Should().Be(1);
        overview.DailyCounts.Sum(d => d.Count).Should().Be(0, "30 天前不进 14 天窗口");
    }

    [Fact]
    public async Task GetOverview_Window_Boundary_Should_Include_Days_Minus_One_And_Exclude_Days()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var boundary = new DateTimeOffset(today.AddDays(-13).ToDateTime(new TimeOnly(12, 0)), DateTimeOffset.Now.Offset);
        var outside = new DateTimeOffset(today.AddDays(-14).ToDateTime(new TimeOnly(12, 0)), DateTimeOffset.Now.Offset);

        var overview = await Build([
                Node("daily/in/a.md", MemoryType.Daily, boundary),
                Node("daily/out/b.md", MemoryType.Daily, outside)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        overview.DailyCounts[0].Count.Should().Be(1, "days-1 天前属于窗口首日");
        overview.DailyCounts.Sum(d => d.Count).Should().Be(1, "正好 days 天前不算");
    }

    [Fact]
    public async Task GetOverview_DailyCounts_Should_Group_By_Local_Date()
    {
        var localNow = DateTimeOffset.Now;
        var overview = await Build([Node("daily/x/a.md", MemoryType.Daily, localNow)])
            .GetOverviewAsync(14, TestContext.Current.CancellationToken);

        var expected = DateOnly.FromDateTime(localNow.LocalDateTime);
        overview.DailyCounts.Single(d => d.Count == 1).Date.Should().Be(expected,
            "按用户本地日历归日，不能用 UTC");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Memory.Tests -v q --nologo 2>&1 | Select-String -Pattern "error CS" | Select-Object -First 5
```

预期：`MemoryOverview` 与 `GetOverviewAsync` 不存在。

- [ ] **Step 3: 建 `MemoryOverview.cs`**

```csharp
using Seeing.Agent.Memory.Core.Graph;
using Seeing.Agent.Memory.Core.Models;

namespace Seeing.Agent.Memory.Abstractions;

/// <summary>某一天的记忆条数。</summary>
public record MemoryDailyCount(DateOnly Date, int Count);

/// <summary>
/// 记忆总览：一次加载得出的全部聚合。
/// <para>
/// <see cref="DailyCounts"/> 长度恒为请求的 <c>days</c>（含 0 值日期），窗口外的记忆
/// 不计入它，但仍计入 <see cref="TotalCount"/> 与 <see cref="CountByType"/>——两者语义不同。
/// </para>
/// </summary>
public record MemoryOverview(
    int TotalCount,
    IReadOnlyDictionary<MemoryType, int> CountByType,
    IReadOnlyList<MemoryDailyCount> DailyCounts,
    IndexStats Index,
    GraphStats Graph);
```

- [ ] **Step 4: 加接口方法**

在 `IMemoryService` 的「统计」区加：

```csharp
/// <summary>
/// 获取总览：类型/时间分布 + 索引与图谱统计。
/// <para>只读一次盘，供总览页单点消费。</para>
/// </summary>
/// <param name="days">时间分布窗口天数（含今天），至少 1。</param>
Task<MemoryOverview> GetOverviewAsync(int days = 14, CancellationToken ct = default);
```

- [ ] **Step 5: 实现 `GetOverviewAsync`**

在 `MemoryService` 里加：

```csharp
/// <inheritdoc />
public async Task<MemoryOverview> GetOverviewAsync(int days = 14, CancellationToken ct = default)
{
    if (days < 1)
        throw new ArgumentOutOfRangeException(nameof(days), days, "时间分布窗口至少 1 天");

    // 只读一次盘：类型分布、时间分布都基于这一份快照。
    var files = await _fileStore.ListAsync(ct: ct);
    var indexStats = await _index.GetStatsAsync(ct);
    var graphStats = await _graph.GetStatsAsync(ct);

    // 三个键恒在，值为 0 也要有：前端四张卡片才能稳定渲染。
    var countByType = new Dictionary<MemoryType, int>
    {
        [MemoryType.Session] = 0,
        [MemoryType.Daily] = 0,
        [MemoryType.Digest] = 0,
    };
    foreach (var file in files)
    {
        countByType.TryGetValue(file.Metadata.Type, out var n);
        countByType[file.Metadata.Type] = n + 1;
    }

    // 逐日补零，长度恒为 days：前端不必补零，柱子数量也不会随数据抖动。
    var today = DateOnly.FromDateTime(DateTime.Now);
    var firstDay = today.AddDays(-(days - 1));
    var buckets = new int[days];
    foreach (var file in files)
    {
        // 按用户本地日历归日：UTC 跨日会把「今晚写的」算到昨天。
        var day = DateOnly.FromDateTime(file.Metadata.CreatedAt.LocalDateTime);
        var offset = day.DayNumber - firstDay.DayNumber;
        if (offset >= 0 && offset < days)
            buckets[offset]++;
    }

    var daily = new List<MemoryDailyCount>(days);
    for (var i = 0; i < days; i++)
        daily.Add(new MemoryDailyCount(firstDay.AddDays(i), buckets[i]));

    return new MemoryOverview(
        TotalCount: files.Count,
        CountByType: countByType,
        DailyCounts: daily,
        Index: indexStats,
        Graph: graphStats);
}
```

- [ ] **Step 6: 跑测试确认通过**

```powershell
dotnet build tests/capabilities/Seeing.Agent.Memory.Tests -v q --nologo
dotnet vstest tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll > "$env:TEMP\t2.txt" 2>&1
"exit=$LASTEXITCODE"
Get-Content "$env:TEMP\t2.txt" -Encoding UTF8 | Where-Object { $_ -match 'net10\.0\)' } | Select-Object -Last 1
```

预期：全绿，总计 > 0。

- [ ] **Step 7: 变异验证**

- 去掉 `daily.Add(...)` 的补零循环（只对有数据的日期加）→ `HaveCount(14)` 必红。
- 把窗口过滤删掉（所有文件都计入 `buckets`，越界就 `offset` 溢出）→ 窗口外用例必红。
- 把 `CreatedAt.LocalDateTime` 换成 `CreatedAt.UtcDateTime` → 本地日历用例在非 UTC 时区下红。

每次都要 `--no-incremental` 重建。

- [ ] **Step 8: 提交**

```bash
git add src/capabilities/Seeing.Agent.Memory/Abstractions/MemoryOverview.cs src/capabilities/Seeing.Agent.Memory/Abstractions/IMemoryService.cs src/capabilities/Seeing.Agent.Memory/Core/MemoryService.cs tests/capabilities/Seeing.Agent.Memory.Tests/Core/MemoryOverviewTests.cs
git commit -m "feat(memory): 新增 GetOverviewAsync 一次读盘出类型与时间分布"
```

---

## Task 3: 重写 `MemoryPage.razor` 为单页总览

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor`（整文件重写）
- Create: `samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor.css`

**Interfaces:**
- Consumes: `IMemoryService.GetOverviewAsync(int days, CancellationToken)`、`IMemoryService.ListAsync`、`IMemoryService.SearchAsync`、`IMemoryService.DeleteAsync`、`IMemoryService.ClearAllAsync`
- Consumes: `MemoryOverview.DailyCounts` / `CountByType` / `TotalCount`
- Produces: 页面路由 `/memory`（不变）；内部方法 `BarHeight(int)`、`ApplyFilter`、`IsSelected(MemoryType?)`

- [ ] **Step 1: 重写 `MemoryPage.razor`**

结构（按此顺序）：

1. `@page "/memory"` 之外的 using/inject 保留原有，新增 `@using Seeing.Agent.Memory.Core.Graph`（`GraphStats` 在 `MemoryOverview` 里）。
2. `<WorkspacePageShell Loading="@_loading" IsEmpty="@IsEmpty">`
3. `<Breadcrumb>`：工作区 / 记忆
4. `<Extra>`：
   - `设置` → `Navigation.NavigateTo("/settings")`（记忆设置的页签在系统设置里）
   - `知识图谱` → `/memory/graph`
   - `刷新` → `LoadAsync`
   - `清空全部`（放最后，仍用 `Popconfirm`，危险样式）
5. `<ChildContent>`：

```razor
@* 空状态：可行动，不留白 *@
@if (IsEmpty)
{
    <div class="empty-state">
        <h3 class="empty-state-title">暂无记忆</h3>
        <p class="empty-state-description">
            记忆在每次对话后自动抽取（默认开启，每 10 轮一次）。
            如果一直为空，请检查设置中的「自动捕获」与「抽取」是否被关闭。
        </p>
        <div class="empty-state-action">
            <Button Type="@ButtonType.Primary" OnClick="@(() => Navigation.NavigateTo("/settings"))">
                打开设置
            </Button>
        </div>
    </div>
}
else
{
    @* 概览：卡片可点击筛选 *@
    <div class="mem-cards">
        <button class="mem-card @(TypeFilter is null ? "is-active" : "")" @onclick="@(() => ApplyFilter(null))">
            <span class="mem-card-value">@Overview!.TotalCount</span>
            <span class="mem-card-label">全部</span>
        </button>
        @foreach (var t in TypeCards)
        {
            <button class="mem-card @(TypeFilter == t.Type ? "is-active" : "")" @onclick="@(() => ApplyFilter(t.Type))">
                <span class="mem-card-value">@Overview!.CountByType[t.Type]</span>
                <span class="mem-card-label">@t.Label</span>
            </button>
        }
    </div>

    @* 时间分布：不可点击，只做趋势感知 *@
    <div class="mem-daily">
        <div class="mem-daily-title">最近 @Days 天</div>
        <div class="mem-bars">
            @foreach (var d in Overview!.DailyCounts)
            {
                <div class="mem-bar-slot" title="@($"{d.Date:MM-dd} · {d.Count} 条")">
                    <div class="mem-bar @(d.Count == 0 ? "is-zero" : "")"
                         style="height:@(BarHeight(d.Count))px"></div>
                </div>
            }
        </div>
        <div class="mem-axis">
            <span>@Overview!.DailyCounts[0].Date.ToString("MM-dd")</span>
            <span>@Overview!.DailyCounts[Days / 2].Date.ToString("MM-dd")</span>
            <span>@Overview!.DailyCounts[^1].Date.ToString("MM-dd")</span>
        </div>
    </div>

    @* 过滤条 *@
    <div class="page-filters">
        <Select TItem="MemoryType?" TItemValue="MemoryType?"
                Value="@TypeFilter" ValueChanged="OnTypeFilterChanged"
                Placeholder="全部类型" AllowClear="true" Style="width: 140px;">
            <SelectOption TItem="MemoryType?" TItemValue="MemoryType?" Value="@((MemoryType?)null)" Label="全部类型" />
            <SelectOption TItem="MemoryType?" TItemValue="MemoryType?" Value="@((MemoryType?)MemoryType.Session)" Label="会话" />
            <SelectOption TItem="MemoryType?" TItemValue="MemoryType?" Value="@((MemoryType?)MemoryType.Daily)" Label="每日" />
            <SelectOption TItem="MemoryType?" TItemValue="MemoryType?" Value="@((MemoryType?)MemoryType.Digest)" Label="整合" />
        </Select>
        <Search Placeholder="搜索记忆（回车走语义检索）..." @bind-Value="_query"
                OnSearch="OnSearch" AllowClear="true" EnterButton="true" Style="width: 360px;" />
        <span class="mem-count">@ListTitle 共 @Rows.Count 条</span>
    </div>

    @* 列表：原生 table，不用 Table/PropertyColumn *@
    <table class="items-table">
        <thead>
            <tr>
                <th>标题</th>
                <th style="width: 80px;">类型</th>
                <th style="width: 170px;">来源会话</th>
                <th style="width: 150px;">修改时间</th>
                <th style="width: 120px;">操作</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var row in PagedRows)
            {
                <tr @key="row.Path">
                    <td>
                        <a href="javascript:void(0)"
                           @onclick="@(() => Navigation.NavigateTo($"/memory/detail/{Uri.EscapeDataString(row.Path)}"))">
                            @row.Title
                        </a>
                    </td>
                    <td><Tag Color="@TypeColor(row.Type)">@TypeLabel(row.Type)</Tag></td>
                    <td title="@row.SourceSession">@row.SourceSessionText</td>
                    <td>@row.ModifiedText</td>
                    <td>
                        <Space>
                            <Button Size="@ButtonSize.Small"
                                    OnClick="@(() => Navigation.NavigateTo($"/memory/detail/{Uri.EscapeDataString(row.Path)}"))">查看</Button>
                            <Popconfirm Title="确定删除此记忆？将同步移除索引与图谱节点。"
                                        OnConfirm="@(() => DeleteAsync(row.Path))">
                                <Button Size="@ButtonSize.Small" Danger>删除</Button>
                            </Popconfirm>
                        </Space>
                    </td>
                </tr>
            }
        </tbody>
    </table>

    @if (Rows.Count > PageSize)
    {
        <Pagination Current="@_page" Total="@Rows.Count" PageSize="@PageSize"
                    OnChange="OnPageChange" ShowSizeChanger="false" />
    }
}
```

6. `@code` 块：

```csharp
private const int Days = 14;
private const int PageSize = 20;

private static readonly (MemoryType Type, string Label)[] TypeCards =
[
    (MemoryType.Session, "会话"),
    (MemoryType.Daily, "每日"),
    (MemoryType.Digest, "整合"),
];

private MemoryOverview? Overview;
private IReadOnlyList<FileNode> _files = [];
private IReadOnlyList<SearchHit>? _semanticHits;
private MemoryType? TypeFilter;
private string _query = "";
private int _page = 1;
private bool _loading = true;
private bool _clearing;

private bool IsEmpty => Overview is { TotalCount: 0 } && string.IsNullOrWhiteSpace(_query);
private string ListTitle => _semanticHits is null ? "全部记忆" : "语义检索结果";

protected override async Task OnInitializedAsync() => await LoadAsync();

private async Task LoadAsync()
{
    _loading = true;
    try
    {
        // 并发但只各读一次；ListAsync 提供列表所需的标题/路径/来源明细。
        var overviewTask = MemoryService.GetOverviewAsync(Days);
        var filesTask = MemoryService.ListAsync();
        await Task.WhenAll(overviewTask, filesTask);
        Overview = await overviewTask;
        _files = await filesTask;
    }
    finally
    {
        _loading = false;
    }
}
```

`Rows` 的投影：语义模式用 `_semanticHits`，否则用 `_files` 经类型过滤 + 本地关键词过滤，
按 `ModifiedTime` 降序（列表排序用修改时间，时间分布用创建时间——两者不混）。
行模型：

```csharp
private sealed class Row
{
    public string Path = "";
    public string Title = "";
    public MemoryType Type;
    public string? SourceSession;
    public string SourceSessionText = "";   // 超过 12 字符截断 + "…"
    public DateTimeOffset Modified;
    public string ModifiedText = "";
}
```

`SourceSessionText`：`string.IsNullOrWhiteSpace(x) ? "—" : (x.Length <= 12 ? x : x[..12] + "…")`。

`ApplyFilter(MemoryType? t)`：点同一类型则取消（`TypeFilter = TypeFilter == t ? null : t`），
重置 `_page = 1`，并清掉 `_semanticHits`（语义结果与类型为不同维度，混在一起没意义）。

`BarHeight(int count)`：

```csharp
private int BarHeight(int count)
{
    var max = Overview?.DailyCounts.Max(d => d.Count) ?? 0;
    if (count <= 0 || max <= 0) return 2;
    return Math.Max(4, (int)Math.Round(count / (double)max * 72));
}
```

`OnSearch(string q)`：非空则调 `SearchAsync(new SearchQuery(Text: q, Mode: SearchMode.Hybrid, Limit: 50))`，
结果存 `_semanticHits`；空则清 `_semanticHits` 回到列表。重置 `_page = 1`。

`OnPageChange(PaginationEventArgs args)`：`_page = args.Page`。
`PagedRows` = `Rows.Skip((_page-1)*PageSize).Take(PageSize)`。

- [ ] **Step 2: 建 `MemoryPage.razor.css`**

见 spec §5.2。必须包含 `.mem-cards` / `.mem-card` / `.mem-card.is-active` /
`.mem-bars` / `.mem-bar-slot` / `.mem-bar` / `.mem-bar.is-zero` / `.mem-axis` / `.mem-count` / `.mem-daily`。

`.mem-cards`：`display:grid; grid-template-columns:repeat(4, 1fr); gap:12px; margin-bottom:16px;`
`.mem-card`：`text-align:left; padding:12px 16px; border:1px solid var(--color-border);
border-radius:var(--radius-md); background:var(--color-bg-secondary); cursor:pointer;`
`.mem-card.is-active`：`border-color:var(--color-primary); background:var(--color-primary-bg);`
`.mem-card-value`：`display:block; font-size:22px; font-weight:600;`
`.mem-card-label`：`display:block; font-size:12px; color:var(--color-text-tertiary);`
`.mem-axis`：`display:flex; justify-content:space-between; font-size:12px; color:var(--color-text-tertiary); margin-top:4px;`

- [ ] **Step 3: 构建 WebUI**

```powershell
dotnet build samples/Seeing.Agent.WebUI -v q --nologo 2>&1 | Select-String -Pattern "error" | Select-Object -First 10
"exit=$LASTEXITCODE"
```

预期：`exit=0`。

- [ ] **Step 4: 起服务人工验证**

```powershell
dotnet run --project samples/Seeing.Agent.WebUI
```

逐条核对：

1. `/memory` 首屏同时出现 4 张卡、14 根柱、列表。
2. 点「会话」→ 列表只剩 session；再点 → 恢复全部。
3. 输入关键词不回车 → 列表本地过滤；回车 → 标题变「语义检索结果」。
4. 清空记忆后 → 空状态有「打开设置」按钮，点击到 `/settings`。
5. 柱状图零值柱可见（浅色细柱），鼠标悬停显示「MM-dd · N 条」。

- [ ] **Step 5: 提交**

```bash
git add samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor.css
git commit -m "feat(webui): 记忆页改为概览+时间分布+列表面板"
```

---

## Task 4: 记忆设置改走 SettingsCard

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Components/Settings/MemorySettingsCard.razor`（由 `MemorySettingsPage.razor` 搬迁）
- Delete: `samples/Seeing.Agent.WebUI/Pages/MemorySettingsPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/CoreShellUiContribution.cs:49-63`（追加一条 SettingsCard）
- Modify: `src/capabilities/Seeing.Agent.Memory/MemoryModule.cs:63-69`（删 `/memory/settings` 的 NavContribution）
- Modify: `samples/Seeing.Agent.WebUI/Services/ModulePageRouteBinder.cs:27-28`（删 `/memory/settings` 绑定）

**Interfaces:**
- Consumes: `IMemoryOptionsStore`、`IModelConfigManager`、`IEmbeddingStatus`、`IEmbeddingConnectionTester`（均沿用原页面）
- Produces: `MemorySettingsCard` 组件类型（无参数，自包含）

- [ ] **Step 1: 建卡片组件**

把 `MemorySettingsPage.razor` 的**全部内容**（`@using` / `@inject` / 5 张 `<Card>` / `@code` 块）搬到
`Components/Settings/MemorySettingsCard.razor`，按 spec §5.3.1 做三处删改：

1. 删除 `<WorkspacePageShell>` 与 `<PageTitle>`、`<Breadcrumb>`、`<Extra>`（含「返回」「保存」按钮）。
2. 保存按钮移到最后一个 `<Card>` 之后，单独一行右对齐：
   ```razor
   <div style="text-align: right; margin-top: 16px;">
       <Button Type="@ButtonType.Primary" Icon="@IconType.Outline.Save"
               OnClick="SaveAsync" Loading="@_saving">保存</Button>
   </div>
   ```
3. 删掉 `@page`。

`@code` 块**原样保留**（`_options` / `_saving` / `_testingEmbedding` / `Clone` / `SaveAsync` /
`TestEmbeddingAsync` / `ResolveEmbeddingKey` / `ResolveExtractionKey` / `OnExtractionKeyChanged` /
`OnEmbeddingKeyChanged` / `RefreshModelKeys`）。

**注意**：原页面的 `_loading` 恒为 `false`（`OnInitialized` 里立即置 false），卡片里可以删掉
`_loading` 字段与相关标记，但 `OnInitialized` 必须保留 `_options = Clone(OptionsStore.Get()); RefreshModelKeys();`。

- [ ] **Step 2: 登记 SettingsCard**

在 `CoreShellUiContribution.cs` 的贡献列表末尾（`SchedulerSettingsCard` 那条之后）追加：

```csharp
new SettingsCardContribution(
    "/settings/memory",
    "记忆",
    typeof(MemorySettingsCard),
    ["memory"]),
```

- [ ] **Step 3: 删除三个位置对 `/memory/settings` 的引用**

1. `MemoryModule.Contribute()`：删掉 `new NavContribution("/memory/settings", ...)` 这一条，
   只留 `/memory`。（该 Nav 本来就被 `FilterSidebarNav` 的前缀规则隐藏，删掉是澄清而非行为变更。）
2. `ModulePageRouteBinder.KnownModuleRoutes`：删掉
   `new("/memory/settings", typeof(MemorySettingsPage), "memory", "记忆设置", "setting", ...)` 这一条。
3. `Pages/MemorySettingsPage.razor`：**删除文件**。

- [ ] **Step 4: 构建**

```powershell
dotnet build Seeing.Agent.slnx -v q --nologo 2>&1 | Select-String -Pattern "error" | Select-Object -First 10
"exit=$LASTEXITCODE"
```

预期：`exit=0`。若有 `MemorySettingsPage` 未找到的编译错误，说明还有残留引用，回到 Step 3 清干净。

- [ ] **Step 5: 人工验证**

1. `/settings` 页签里有「记忆」；
2. 点进去能看到捕获/抽取/演化/Embedding/召回 5 张卡；
3. 改「每 N 轮抽取」→ 点「保存」→ 提示「记忆设置已保存」；
4. 刷新页面 → 值保持（写进 `seeing.json` 的 `Memory` 节）；
5. 停用 memory 模块后重启 → 「记忆」页签消失（`Requires=["memory"]` 生效）。

- [ ] **Step 6: 提交**

```bash
git add -A samples/Seeing.Agent.WebUI/Components/Settings/MemorySettingsCard.razor samples/Seeing.Agent.WebUI/Pages/MemorySettingsPage.razor samples/Seeing.Agent.WebUI/Services/CoreShellUiContribution.cs src/capabilities/Seeing.Agent.Memory/MemoryModule.cs samples/Seeing.Agent.WebUI/Services/ModulePageRouteBinder.cs
git commit -m "refactor(webui): 记忆设置改为设置页签，删除 /memory/settings 页面"
```

---

## Task 5: 删除 browse/stats 页面并清理所有导航引用

**Files:**
- Delete: `samples/Seeing.Agent.WebUI/Pages/MemoryBrowsePage.razor`
- Delete: `samples/Seeing.Agent.WebUI/Pages/MemoryStatsPage.razor`
- Modify: `samples/Seeing.Agent.WebUI/Services/ModulePageRouteBinder.cs:29-34`
- Modify: `samples/Seeing.Agent.WebUI/Pages/MemoryDetailPage.razor:14-22`
- Modify: `samples/Seeing.Agent.WebUI/Pages/MemoryGraphPage.razor:20`
- Modify: `samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor`（若 Step 1 的 Extra 里还留着旧按钮）

**Interfaces:**
- Consumes: 无
- Produces: 无（纯删除与改指向）

- [ ] **Step 1: 删除两个页面与路由表条目**

删除 `MemoryBrowsePage.razor`、`MemoryStatsPage.razor`。
`ModulePageRouteBinder.KnownModuleRoutes` 删除 `/memory/browse`（Order 52）与 `/memory/stats`（Order 54）两条。
删除后 memory 模块只剩：`/memory`(50)、`/memory/graph`(53)、`/memory/detail/{*FilePath}`(55)。

- [ ] **Step 2: 改详情页导航**

`MemoryDetailPage.razor`：

- 面包屑删除 `<BreadcrumbItem Href="/memory/browse">目录浏览</BreadcrumbItem>`，
  只留 `<BreadcrumbItem Href="/memory">记忆</BreadcrumbItem>` + 标题。
- 「返回列表」按钮 `Navigation.NavigateTo("/memory/browse")` → `"/memory"`。
- `@code` 里若有 `NavigateTo("/memory/browse")`（删除后跳转），改为 `"/memory"`。

- [ ] **Step 3: 改图谱页导航**

`MemoryGraphPage.razor` 的 `<Extra>`：

- 「记忆搜索」按钮改文案 `记忆总览`，目标 `/memory`（不变）。
- 删除「目录浏览」「统计」按钮（若存在）。

- [ ] **Step 4: 全仓确认无残留**

```powershell
Select-String -Path "samples/Seeing.Agent.WebUI/**/*.razor","samples/Seeing.Agent.WebUI/**/*.cs" -Pattern "memory/browse|memory/stats|MemoryBrowsePage|MemoryStatsPage"
```

预期：无输出。若有输出，逐条改掉。

- [ ] **Step 5: 构建**

```powershell
dotnet build Seeing.Agent.slnx -v q --nologo 2>&1 | Select-String -Pattern "error" | Select-Object -First 10
"exit=$LASTEXITCODE"
```

预期：`exit=0`。

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "refactor(webui): 删除记忆浏览与统计页，导航统一到 /memory"
```

---

## Task 6: 全量验证与文档

**Files:**
- Modify: `docs/architecture/06-compliance-audit.md`（若涉及依赖方向变更——本计划不涉及，跳过）
- Create: `docs/architecture/26-memory-page-redesign-release-notes.md`

**Interfaces:**
- Consumes: 前 5 个任务的全部产出
- Produces: 无

- [ ] **Step 1: 全量重建**

```powershell
dotnet build Seeing.Agent.slnx -v q --nologo --no-incremental 2>&1 | Select-String -Pattern "error" | Select-Object -First 10
"build=$LASTEXITCODE"
```

预期：`build=0`。

- [ ] **Step 2: 跑全部测试套件**

```powershell
$suites = @(
 @("spine","tests/spine/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll"),
 @("memory","tests/capabilities/Seeing.Agent.Memory.Tests/bin/Debug/net10.0/Seeing.Agent.Memory.Tests.dll"),
 @("hosting","tests/hosting/Seeing.Agent.Hosting.Tests/bin/Debug/net10.0/Seeing.Agent.Hosting.Tests.dll"),
 @("hostcomp","tests/apps/Seeing.Agent.HostComposition.Tests/bin/Debug/net10.0/Seeing.Agent.HostComposition.Tests.dll"),
 @("reflection","tests/capabilities/Seeing.Agent.Reflection.Tests/bin/Debug/net10.0/Seeing.Agent.Reflection.Tests.dll"),
 @("metadata","tests/capabilities/Seeing.Agent.Tools.Metadata.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Metadata.Tests.dll"),
 @("session","tests/primitives/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll"),
 @("tui","tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll"),
 @("cli","tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll")
)
foreach ($s in $suites) {
  dotnet vstest $s[1] > "$env:TEMP\final_$($s[0]).txt" 2>&1
  $code = $LASTEXITCODE
  $last = Get-Content "$env:TEMP\final_$($s[0]).txt" -Encoding UTF8 | Where-Object { $_ -match 'net10\.0\)' } | Select-Object -Last 1
  "$($s[0]) exit=$code :: $last"
}
```

预期：**9 个套件全部 `exit=0` 且「总计」> 0**。基线是 2966 用例（memory 221）。
实际用例数：Task 1 有 5 条 `[Fact]`，Task 2 有 6 条 `[Fact]`，共 11 条。
若实际总数与「基线 + 11」不符，先查新增用例是否真的被执行。

> 若实际总数与 232 不符：**先查新增用例是否真的被执行**（逐套件确认「总计」> 0，
> 过滤器拼错会导致 0 用例执行且 exit 0）。数量对不上时不要直接认定通过。

- [ ] **Step 3: 写 release notes**

新建 `docs/architecture/26-memory-page-redesign-release-notes.md`，内容必须包含：

- **变更摘要**：单页总览、删除 3 个页面、设置改 SettingsCard。
- **行为变更**：`/memory/browse`、`/memory/stats`、`/memory/settings` 直链失效。
- **修复的缺陷**：`created_at` 键名错配（附「症状 → 根因 → 修法」三段）；`GetDateTime` 判定顺序。
- **新增契约**：`MemoryOverview` / `MemoryDailyCount` / `GetOverviewAsync`。
- **已知边界**（照抄 spec §8）：`ExpiresAt` 仍坏且无消费者；柱状图不可点击；
  `/memory/settings` 不能作为侧栏项是前缀规则的必然结果；`Core/MemoryType.cs` 死枚举未清。
- **验证记录**：9 套件用例数与结果。

- [ ] **Step 4: 更新 `AGENTS.md` 的 WHERE TO LOOK**

在「WebUI 模型选择」一行之后加：

```
| 记忆页面 | `samples/Seeing.Agent.WebUI/Pages/MemoryPage.razor` + `Abstractions/MemoryOverview.cs` | 单页总览：`GetOverviewAsync` 一次读盘出类型/时间分布；时间分布用 `CreatedAt`（本地日历归日），列表排序用 `ModifiedTime`；设置走 `SettingsCardContribution`；见 `docs/architecture/26` |
```

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "docs: 记忆页面重设计 release notes 与 AGENTS 索引"
```

---

## Self-Review

**Spec 覆盖检查：**

| Spec 章节 | 对应任务 |
|---|---|
| §1.4 `created_at` 修复 | Task 1 |
| §4.1 mtime 回落 + `GetDateTime` 顺序 | Task 1 Step 5/6 |
| §4.2 `SourceSession` | Task 1 Step 3/5 |
| §4.3 `GetOverviewAsync` 聚合 | Task 2 |
| §3.1 路由变更 | Task 4（settings）、Task 5（browse/stats） |
| §3.3 交互（卡片筛选、空状态） | Task 3 |
| §5.1 页面重写 | Task 3 |
| §5.2 柱状图 | Task 3 Step 2 |
| §5.3.1 设置卡三处改造 | Task 4 Step 1 |
| §6.1 后端测试 | Task 1 Step 1、Task 2 Step 1 |
| §6.2 变异验证 | Task 1 Step 8、Task 2 Step 7 |
| §6.3 前端人工验收 | Task 3 Step 4、Task 4 Step 5 |

**Review Focus 五项落地：**
1. 无 `created_at` → Task 1 `ReadAsync_Without_CreatedAt_...`
2. 窗口边界 → Task 2 `GetOverview_Window_Boundary_...`
3. 空知识库 → Task 3 `IsEmpty` + Step 4 验收项 4
4. `CreatedAt` vs `ModifiedTime` → Task 3 `Rows` 按 `ModifiedTime` 排序，`DailyCounts` 用 `CreatedAt`
5. 仅 session 有数据 → Task 2 `GetOverview_CountByType_Should_Include_Zero_Value_Types`

**类型一致性：** `MemoryOverview` / `MemoryDailyCount` / `GetOverviewAsync` / `SourceSession` /
`BarHeight` 在各任务中命名一致。

**比例：** 计划含少量关键代码块（复用于易错的解析逻辑与聚合逻辑），其余为签名与断言。
