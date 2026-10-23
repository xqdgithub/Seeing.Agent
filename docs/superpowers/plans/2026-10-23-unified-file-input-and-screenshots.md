# 统一文件输入与截图 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 WebUI 的粘贴/拖拽/文件选择器与 TUI 的 Ctrl+V、`@path`、`/attach` 统一为“图片进附件、普通文件进路径”，并消除 Blazor 32 KB 与终端 Ctrl+V 的限制。

**Architecture:** 共享 `PastedFileLayout` 统一上传目录、命名和 prompt 路径；Web 用 HTTP 上传（选择器复用 C# 落盘服务），TUI 用 bracketed-paste 检测 + Windows 剪贴板读取。普通文件不进 `ChatAttachment`，而是作为路径写入消息正文；图片继续走现有图片附件链路。

**Tech Stack:** .NET 10、Blazor Server、Ant Design Blazor、xUnit + FluentAssertions + Moq、Spectre.Console TUI、Windows Win32/PowerShell 剪贴板。

**Spec:** `docs/superpowers/specs/2026-10-23-paste-files-and-screenshots-design.md`

## Global Constraints

- Blazor Server 默认单条消息上限 32 KB；粘贴/拖拽的文件字节必须走 HTTP，不得经 JSInterop。
- 文件选择器使用 `InputFile` 分块流，不复用 HTTP 端点，但复用同一个落盘服务和布局。
- 浏览器拿不到本地绝对路径；Web 端一律上传，不假设本机路径。
- 上传根：`<session effective root>/.seeing/uploads/<safeSessionId>/`。
- prompt 路径基准：项目根；项目根内用正斜杠相对路径，项目根外用绝对路径，反引号包裹，一行一个。
- 图片附件上限 10 MB；普通文件上传上限 50 MB；单次上传最多 10 个；单次 HTTP 请求体上限 100 MB。
- 普通文件不得进入 `ChatAttachment`；图片才进入。
- `IFileSystem` 是宿主/工具的唯一文件 I/O seam；不要直接调用 `File.*`（`FileWorkspace` 实现内部除外）。
- 回调中的 `SessionRelativePath` 视为不可信输入；读/删必须校验在 uploads 目录内，并传绝对路径给 `IFileWorkspace`。
- TUI 图片剪贴板先实现 Windows；其他平台优雅降级，文本不受影响。
- 不新增 `/paste` 命令；TUI 入口是 Ctrl+V。
- 时间戳使用本地时区；`.seeing/` 已 gitignore。
- 测试运行方式：`dotnet build <test project>` 后用 `dotnet vstest <dll> --TestCaseFilter:...`（见根 AGENTS.md；MTP 在本环境不可发现）。

## Review Focus

- 粘贴/拖拽超过 32 KB 的截图：必须成功进入附件，不能静默失败或截断。
- TUI 空 bracketed paste 与 0x16：不能吞掉文本插入，也不能把 Ctrl+V 退化成字面 `v`。
- 会话有效根与项目根不同：上传/图片读回/chip 删除必须仍落在会话根，prompt 路径必须能被工具解析。
- 回调 `SessionRelativePath` 被篡改：rooted、`..`、越出 uploads 目录时必须拒绝读和删。
- 选择器/拖拽的非图片文件改走路径，以及 TUI `/detach` 只管理图片：不能残留旧的 `ChatAttachment` 或删除路径文本。

---

### Task 1: 共享路径布局 `PastedFileLayout`

**Files:**
- Create: `src/support/Seeing.Agent.Tools.Support/FilePaste/PastedFileLayout.cs`
- Modify: `samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj`（加 `ProjectReference` 到 `src/support/Seeing.Agent.Tools.Support`）
- Modify: `samples/Seeing.Agent.Tui/Seeing.Agent.Tui.csproj`（同上）
- Test: `tests/support/Seeing.Agent.Tools.Support.Tests/FilePaste/PastedFileLayoutTests.cs`

**Interfaces:**
- Consumes: 无。
- Produces:
```csharp
namespace Seeing.Agent.Tools.Support;

public static class PastedFileLayout
{
    public const string RootDirectory = ".seeing/uploads";
    public const long MaxImageBytes = 10L * 1024 * 1024;
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int MaxFilesPerRequest = 10;
    public const int MaxUploadRequestBytes = 100 * 1024 * 1024;

    public static string SafeSessionId(string sessionId);
    public static string BuildSessionDirectory(string sessionRoot, string sessionId);
    public static string BuildStoredFileName(string originalName, DateTimeOffset now, string suffix);
    public static string BuildAbsolutePath(string sessionRoot, string sessionId, string storedFileName);
    public static bool TryGetProjectRelativePath(string projectRoot, string absolutePath, out string relativePath);
    public static string ToPromptPath(string projectRoot, string absolutePath);
    public static string FormatPromptBlock(IReadOnlyList<string> promptPaths);
    public static string AppendPromptPaths(string text, IReadOnlyList<string> promptPaths);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class PastedFileLayoutTests
{
    [Fact]
    public void SafeSessionId_ShouldKeepSafeCharactersAndReplaceOthers()
        => PastedFileLayout.SafeSessionId("a/b:c").Should().Be("a_b_c");

    [Fact]
    public void BuildStoredFileName_ShouldKeepReadableBaseAndExtension()
        => PastedFileLayout.BuildStoredFileName(
                "my report.pdf",
                new DateTimeOffset(2026, 10, 23, 15, 30, 12, TimeSpan.Zero),
                "a1b2")
            .Should().Be("my report-20261023-153012-a1b2.pdf");

    [Fact]
    public void TryGetProjectRelativePath_Inside_ShouldUseForwardSlashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "seeing-layout");
        var file = Path.Combine(root, ".seeing", "uploads", "s1", "a.txt");
        PastedFileLayout.TryGetProjectRelativePath(root, file, out var relative).Should().BeTrue();
        relative.Should().Be(".seeing/uploads/s1/a.txt");
    }

    [Fact]
    public void ToPromptPath_Outside_ShouldReturnAbsolutePath()
    {
        var project = Path.Combine(Path.GetTempPath(), "p");
        var other = Path.Combine(Path.GetTempPath(), "o", "a.txt");
        PastedFileLayout.ToPromptPath(project, other)
            .Should().Be(other.Replace('\\', '/'));
    }

    [Fact]
    public void FormatPromptBlock_ShouldWrapEachPathInBackticks()
        => PastedFileLayout.FormatPromptBlock(["a.txt", "b/c.pdf"])
            .Should().Be("`a.txt`\n`b/c.pdf`");

    [Fact]
    public void AppendPromptPaths_ShouldAddExactlyOneNewline()
    {
        PastedFileLayout.AppendPromptPaths("hello", ["a.txt"])
            .Should().Be("hello\n`a.txt`");
        PastedFileLayout.AppendPromptPaths("", ["a.txt"])
            .Should().Be("`a.txt`");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/support/Seeing.Agent.Tools.Support.Tests
dotnet vstest tests/support/Seeing.Agent.Tools.Support.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Support.Tests.dll --TestCaseFilter:"FullyQualifiedName~PastedFileLayoutTests"
```
Expected: FAIL（`PastedFileLayout` 不存在）。

- [ ] **Step 3: Implement `PastedFileLayout`**

实现规则按 spec §5.1：
- `BuildSessionDirectory`：`Path.Combine(sessionRoot, RootDirectory, SafeSessionId(sessionId))`。
- `BuildStoredFileName`：清洗 `Path.GetFileName`、控制字符和 `<>:"/\|?*`；base 最长 80；空则 `pasted`；格式 `{base}-{now:yyyyMMdd-HHmmss}-{suffix}{ext}`。
- `TryGetProjectRelativePath`：`Path.GetFullPath`；不在 root 下返回 false；结果替换 `\\` 为 `/`。
- `ToPromptPath`：相对成功则返回相对；否则返回绝对路径（正斜杠）。
- `FormatPromptBlock`：每个路径包反引号、`\n` 连接。
- `AppendPromptPaths`：无路径返回原文本；空文本返回块；否则 `TrimEnd() + "\n" + block`。

- [ ] **Step 4: Run tests to verify they pass**

Run:
```powershell
dotnet build tests/support/Seeing.Agent.Tools.Support.Tests
dotnet vstest tests/support/Seeing.Agent.Tools.Support.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Support.Tests.dll --TestCaseFilter:"FullyQualifiedName~PastedFileLayoutTests"
```
Expected: PASS。

- [ ] **Step 5: Commit**

```powershell
git add src/support/Seeing.Agent.Tools.Support/FilePaste/PastedFileLayout.cs tests/support/Seeing.Agent.Tools.Support.Tests/FilePaste/PastedFileLayoutTests.cs samples/Seeing.Agent.WebUI/Seeing.Agent.WebUI.csproj samples/Seeing.Agent.Tui/Seeing.Agent.Tui.csproj
git commit -m "feat: add unified pasted file layout"
```

---

### Task 2: 二进制写能力（`IFileSystem` / `IFileWorkspace`）

**Files:**
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Execution/IFileSystem.cs`
- Modify: `src/capabilities/Seeing.IO.Local/LocalFileSystem.cs`
- Modify: `src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspace.cs`
- Modify: `src/capabilities/Seeing.Agent.Files/FileWorkspace.cs`
- Test: `tests/capabilities/Seeing.IO.Local.Tests/LocalFileSystemBinaryWriteTests.cs`
- Test: `tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceBinaryWriteTests.cs`
- Modify: `tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/ReadToolFileSystemTests.cs`（`InMemoryFileSystem` 补实现）
- Modify: `tests/capabilities/Seeing.Agent.Files.Tests/GhostEntryFileSystem.cs`（补实现）

**Interfaces:**
- Consumes: 无。
- Produces:
```csharp
// IFileSystem
Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

// IFileWorkspace
Task WriteBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken ct = default);
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class LocalFileSystemBinaryWriteTests
{
    [Fact]
    public async Task WriteAllBytesAsync_ShouldWriteExactBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-{Guid.NewGuid():N}.bin");
        try
        {
            await new LocalFileSystem().WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
            File.ReadAllBytes(path).Should().Equal(1, 2, 3);
        }
        finally { File.Delete(path); }
    }
}

public class FileWorkspaceBinaryWriteTests : IDisposable
{
    // 按 tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceTests.cs 的构造方式
    // 使用 LocalFileSystem + AllowAllGate + FileWatcherPool + ProcessPathWriteLock
    [Fact]
    public async Task WriteBytesAsync_ShouldCreateParentAndRoundtrip()
    {
        var file = Path.Combine(_root, ".seeing", "uploads", "s1", "a.bin");
        await _ws.WriteBytesAsync(file, new byte[] { 9, 8, 7 });
        Directory.Exists(Path.GetDirectoryName(file)).Should().BeTrue();
        (await _ws.ReadBytesAsync(file)).Should().Equal(9, 8, 7);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/capabilities/Seeing.IO.Local.Tests
dotnet vstest tests/capabilities/Seeing.IO.Local.Tests/bin/Debug/net10.0/Seeing.IO.Local.Tests.dll --TestCaseFilter:"FullyQualifiedName~LocalFileSystemBinaryWriteTests"
dotnet build tests/capabilities/Seeing.Agent.Files.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileWorkspaceBinaryWriteTests"
```
Expected: FAIL（接口方法不存在）。

- [ ] **Step 3: Implement the interfaces and implementations**

- `LocalFileSystem.WriteAllBytesAsync` 直接调用 `File.WriteAllBytesAsync`。
- `FileWorkspace.WriteBytesAsync`：
  - `full = Path.GetFullPath(path)`；
  - `EnsureAllowed(full)`；
  - 创建父目录（`_fs.CreateDirectory`）；
  - `using var _ = await _locks.AcquireAsync(full, ct)`；
  - `await _fs.WriteAllBytesAsync(full, content, ct)`。
- 不修改现有 `WriteAsync` 的可编辑性检查。
- 给两个测试替身 `InMemoryFileSystem` / `GhostEntryFileSystem` 补 `WriteAllBytesAsync`（写入内存字典或委托给 `File.WriteAllBytesAsync`）。

- [ ] **Step 4: Run tests to verify they pass**

Run same commands as Step 2.
Expected: PASS。

- [ ] **Step 5: Commit**

```powershell
git add src/abstractions/Seeing.Agent.Abstractions/Execution/IFileSystem.cs src/capabilities/Seeing.IO.Local/LocalFileSystem.cs src/abstractions/Seeing.Agent.Abstractions/Files/IFileWorkspace.cs src/capabilities/Seeing.Agent.Files/FileWorkspace.cs tests/capabilities/Seeing.IO.Local.Tests/LocalFileSystemBinaryWriteTests.cs tests/capabilities/Seeing.Agent.Files.Tests/FileWorkspaceBinaryWriteTests.cs tests/capabilities/Seeing.Agent.Tools.FileSystem.Tests/ReadToolFileSystemTests.cs tests/capabilities/Seeing.Agent.Files.Tests/GhostEntryFileSystem.cs
git commit -m "feat: support binary workspace writes"
```

---

### Task 3: WebUI 附件模型与提交分流

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Models/AttachmentViewModel.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/PromptAttachmentRouter.cs`
- Modify: `samples/Seeing.Agent.WebUI/Components/SessionWindow.razor`（`OnSubmitMessage` 的输入构造与 `ExtractBase64Data`）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/PromptAttachmentRouterTests.cs`

**Interfaces:**
- Consumes: `PastedFileLayout.AppendPromptPaths`（Task 1）。
- Produces:
```csharp
public enum AttachmentDeliveryKind
{
    BinaryAttachment = 0,
    PathReference = 1
}

// AttachmentViewModel 新增
public AttachmentDeliveryKind Kind { get; set; }
public string? PromptPath { get; set; }
public string? SessionRelativePath { get; set; }
public bool IsPathReference => Kind == AttachmentDeliveryKind.PathReference;

// PromptAttachmentRouter
public sealed record PromptAttachmentRoute(string Text, List<ChatAttachment> Attachments);

public static class PromptAttachmentRouter
{
    public static PromptAttachmentRoute Route(
        string text,
        IReadOnlyList<AttachmentViewModel> attachments);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class PromptAttachmentRouterTests
{
    [Fact]
    public void Route_Image_ShouldCreateChatAttachmentAndStripDataPrefix()
    {
        var route = PromptAttachmentRouter.Route("hi", [
            new AttachmentViewModel {
                Kind = AttachmentDeliveryKind.BinaryAttachment,
                Status = AttachmentUploadStatus.Completed,
                FileName = "a.png",
                FileType = "image/png",
                Base64Content = "data:image/png;base64,AQID"
            }
        ]);

        route.Attachments.Should().ContainSingle();
        route.Attachments[0].Base64Data.Should().Be("AQID");
        route.Text.Should().Be("hi");
    }

    [Fact]
    public void Route_PathReference_ShouldAppendBacktickedPathToText()
    {
        var route = PromptAttachmentRouter.Route("hi", [
            new AttachmentViewModel {
                Kind = AttachmentDeliveryKind.PathReference,
                Status = AttachmentUploadStatus.Completed,
                PromptPath = ".seeing/uploads/s1/a.pdf"
            }
        ]);

        route.Attachments.Should().BeEmpty();
        route.Text.Should().Be("hi\n`.seeing/uploads/s1/a.pdf`");
    }

    [Fact]
    public void Route_Mixed_ShouldSplitCorrectly()
    {
        var route = PromptAttachmentRouter.Route("", [
            new AttachmentViewModel { Kind = AttachmentDeliveryKind.BinaryAttachment, Status = AttachmentUploadStatus.Completed, FileType = "image/png", Base64Content = "AQID" },
            new AttachmentViewModel { Kind = AttachmentDeliveryKind.PathReference, Status = AttachmentUploadStatus.Completed, PromptPath = "a.txt" }
        ]);

        route.Attachments.Should().ContainSingle();
        route.Text.Should().Be("`a.txt`");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~PromptAttachmentRouterTests"
```
Expected: FAIL（类型不存在）。

- [ ] **Step 3: Implement model and router**

- 在 `AttachmentViewModel` 增加枚举/属性；`IsImage` 保持 `FileType` 判断。
- `PromptAttachmentRouter.Route`：
  - 图片且 `Completed` → `ChatAttachment { FileName, MimeType=FileType, Base64Data=StripDataPrefix(Base64Content) }`；
  - `PathReference` 且 `Completed` → 收集 `PromptPath`；
  - `route.Text = PastedFileLayout.AppendPromptPaths(text, paths)`；
  - `StripDataPrefix` 从 `SessionWindow` 的私有方法迁移（保持 `data:...;base64,` 去除逻辑）。
- `SessionWindow.OnSubmitMessage` 用 router 替换原来的 `input = new ChatInput { ... }` 构造：
```csharp
var routed = PromptAttachmentRouter.Route(args.Text, args.Attachments);
var input = new ChatInput
{
    Text = routed.Text,
    Attachments = routed.Attachments.Count > 0 ? routed.Attachments.ToList() : null
};
```
- 删除 `SessionWindow` 中不再使用的 `ExtractBase64Data`。

- [ ] **Step 4: Run tests to verify they pass**

Run same command as Step 2.
Expected: PASS。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.WebUI/Models/AttachmentViewModel.cs samples/Seeing.Agent.WebUI/Services/PromptAttachmentRouter.cs samples/Seeing.Agent.WebUI/Components/SessionWindow.razor tests/apps/Seeing.Agent.WebUI.Tests/Services/PromptAttachmentRouterTests.cs
git commit -m "feat(webui): route image attachments and file path references"
```

---

### Task 4: WebUI 落盘服务与上传端点

**Files:**
- Create: `samples/Seeing.Agent.WebUI/Models/UploadedFileItem.cs`
- Create: `samples/Seeing.Agent.WebUI/Services/UploadedFileService.cs`
- Create: `samples/Seeing.Agent.WebUI/Endpoints/UploadEndpoints.cs`
- Modify: `samples/Seeing.Agent.WebUI/Program.cs`（注册 Scoped 服务、Kestrel 请求体上限、映射端点）
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Services/UploadedFileServiceTests.cs`
**Interfaces:**
- Consumes: `PastedFileLayout`（Task 1）、`WriteBytesAsync`（Task 2）、`AttachmentViewModel`（Task 3）。
- Produces:
```csharp
public sealed record UploadedFileItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sessionRelativePath")] string SessionRelativePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mimeType")] string MimeType);

public sealed record StoredUpload(
    AttachmentDeliveryKind Kind,
    string FileName,
    string SessionRelativePath,
    string PromptPath,
    long Size,
    string MimeType);

public sealed class UploadedFileService
{
    public UploadedFileService(IFileWorkspaceFactory workspaces, IWorkspaceProvider project);

    public Task<StoredUpload> StoreAsync(
        string sessionId, string originalName, string? contentType,
        ReadOnlyMemory<byte> content, CancellationToken ct = default);

    public Task<AttachmentViewModel> CreatePathReferenceAsync(
        string sessionId, UploadedFileItem item, CancellationToken ct = default);

    public Task<AttachmentViewModel> CreateImageAttachmentAsync(
        string sessionId, UploadedFileItem item, CancellationToken ct = default);

    public Task DeleteAsync(
        string sessionId, string sessionRelativePath, CancellationToken ct = default);

    public static string ResolveUploadPath(
        string sessionRoot, string sessionId, string sessionRelativePath);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class UploadedFileServiceTests
{
    [Fact]
    public async Task StoreAsync_ShouldWriteUnderSessionUploadsAndComputeRelativePromptPath()
    {
        // Mock factory 返回 RootPath = sessionRoot 的 IFileWorkspace；
        // Mock IWorkspaceProvider.GetProjectRoot() = sessionRoot。
        // 调用 StoreAsync("s1", "a.txt", "text/plain", bytes)。
        // 断言 sessionRelativePath 以 ".seeing/uploads/s1/" 开头；
        // PromptPath == sessionRelativePath；
        // workspace.WriteBytesAsync 收到绝对路径；
        // factory.Release("s1") 被调用。
    }

    [Fact]
    public async Task CreatePathReferenceAsync_RootedOrTraversalPath_ShouldThrow()
    {
        // 使用 "C:\\Windows\\x" 与 "../x" 两种 sessionRelativePath；
        // 断言抛出 InvalidOperationException，且不调用 ReadBytesAsync/DeleteAsync。
    }

    [Fact]
    public async Task CreateImageAttachmentAsync_ShouldReadDeleteAndBuildDataUrl()
    {
        // Mock ReadBytesAsync 返回 [1,2,3]；断言 Base64Content == "data:image/png;base64,AQID"；
        // DeleteAsync 被调用；SessionRelativePath 保留。
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~UploadedFileServiceTests"
```
Expected: FAIL（服务/类型不存在）。

- [ ] **Step 3: Implement service and endpoint**

- `UploadedFileService`：
  - `StoreAsync`：判断 `contentType.StartsWith("image/")`；按类型检查 10 MB/50 MB；`ResolveAsync` 取会话根；`BuildSessionDirectory` + `BuildStoredFileName`；`WriteBytesAsync`；`PromptPath = PastedFileLayout.ToPromptPath(_project.GetProjectRoot(), absolute)`；`SessionRelativePath = Path.GetRelativePath(ws.RootPath, absolute).Replace('\\','/')`；`finally { _workspaces.Release(sessionId); }`。
  - `ResolveUploadPath`：拒绝 `string.IsNullOrWhiteSpace`、`Path.IsPathRooted`、含 `..` 段、控制字符；`uploadsRoot = Path.GetFullPath(Path.Combine(sessionRoot, PastedFileLayout.RootDirectory, PastedFileLayout.SafeSessionId(sessionId)))`；`full = Path.GetFullPath(Path.Combine(sessionRoot, sessionRelativePath))`；`full` 必须以 `uploadsRoot + separator` 开头（Windows 用 `OrdinalIgnoreCase`）；否则抛 `InvalidOperationException`。
  - `CreatePathReferenceAsync`：解析绝对路径；返回 `AttachmentViewModel { Kind=PathReference, PromptPath=..., SessionRelativePath=..., FileName, FileSize, FileType, Status=Completed }`。
  - `CreateImageAttachmentAsync`：解析绝对路径；`ReadBytesAsync`；≤10MB；`DeleteAsync`；Base64 data URL；返回 `BinaryAttachment`。
  - `DeleteAsync`：解析绝对路径；文件不存在时忽略。
- `UploadEndpoints`：
  - `MapPost("/api/uploads", async (HttpRequest request, string sessionId, UploadedFileService service, ISessionManager sessions, CancellationToken ct) => ...)`。
  - 校验 `sessionId` 存在；`request.HasFormContentType`；`await request.ReadFormAsync(ct)`；文件数 ≤10；每个 `IFormFile` 用限长 `MemoryStream` 读取；调用 `StoreAsync`；返回 `Results.Json(new { items })`，每项含 `kind`（`StoredUpload.Kind == BinaryAttachment ? "image" : "file"`）、`fileName`、`sessionRelativePath`、`size`、`mimeType`。
- `Program.cs`：
  - `builder.Services.AddScoped<UploadedFileService>();`
  - `builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = PastedFileLayout.MaxUploadRequestBytes);`
  - `app.MapUploadEndpoints();`（放在 `MapBlazorHub` 之后、`MapFallbackToPage` 之前）。

- [ ] **Step 4: Run tests to verify they pass**

Run same command as Step 2.
Expected: PASS。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.WebUI/Models/UploadedFileItem.cs samples/Seeing.Agent.WebUI/Services/UploadedFileService.cs samples/Seeing.Agent.WebUI/Endpoints/UploadEndpoints.cs samples/Seeing.Agent.WebUI/Program.cs tests/apps/Seeing.Agent.WebUI.Tests/Services/UploadedFileServiceTests.cs
git commit -m "feat(webui): add unified upload service and endpoint"
```

---

### Task 5: WebUI 粘贴/拖拽 JS 与回调

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/js/app.js`
- Modify: `samples/Seeing.Agent.WebUI/Components/PromptInput.razor`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Infrastructure/UploadScriptContractTests.cs`

**Interfaces:**
- Consumes: `UploadedFileService`（Task 4）、`UploadedFileItem`、`AttachmentViewModel`。
- Produces:
```csharp
// app.js
async function uploadFiles(files, sessionId, dotNetRef)
function setupPasteHandler(containerId, dotNetRef)
function setupDropZone(containerId, dotNetRef)
function disposePasteHandler(containerId)
function disposeDropZone(containerId)

// PromptInput.razor
[JSInvokable] Task HandleUploadedFiles(UploadedFileItem[] items)
[JSInvokable] Task HandleUploadFailed(string message)
```

- [ ] **Step 1: Write the failing source-contract tests**

```csharp
public class UploadScriptContractTests
{
    private static string RepoRoot => ...; // 复用 WebUiScriptInteropContractTests.FindRepoRoot 方式
    private static string AppJs => File.ReadAllText(Path.Combine(RepoRoot, "samples", "Seeing.Agent.WebUI", "wwwroot", "js", "app.js"));

    [Fact]
    public void AppJs_ShouldDefineUploadFilesAndPasteDropHandlers()
    {
        AppJs.Should().Contain("async function uploadFiles(");
        AppJs.Should().Contain("function setupPasteHandler(");
        AppJs.Should().Contain("function setupDropZone(");
        AppJs.Should().Contain("disposePasteHandler");
    }

    [Fact]
    public void AppJs_ShouldNotBase64FilesBeforeInterop()
    {
        AppJs.Should().NotContain("fileToBase64(file)");
        AppJs.Should().Contain("/api/uploads");
        AppJs.Should().Contain("FormData");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~UploadScriptContractTests"
```
Expected: FAIL。

- [ ] **Step 3: Implement JS and PromptInput callback**

- `app.js`：
  - `setupPasteHandler(containerId, dotNetRef)`：绑定容器；`paste` 时从 `clipboardData.files` 取文件，空则遍历 `items` 的 `kind === 'file'`；无文件直接返回；有文件 `preventDefault`，调用 `uploadFiles`。
  - `setupDropZone(containerId, dotNetRef)`：`dragover/drop` 只负责视觉态；`drop` 取 `dataTransfer.files`，调用同一个 `uploadFiles`。
  - `uploadFiles(files, sessionId, dotNetRef)`：客户端检查数量/大小；`FormData` append 每个文件；`fetch('/api/uploads?sessionId=' + encodeURIComponent(sessionId), { method: 'POST', body: form })`；成功后 `dotNetRef.invokeMethodAsync('HandleUploadedFiles', result.items)`；失败调用 `HandleUploadFailed`。
  - 容器用 `data-session-id` 读取会话；监听器保存在 `container.__seeingPasteHandler` / `__seeingDropHandler`，`dispose*` 移除。
  - 删除不再使用的 `fileToBase64`/`getFileInfo` 旧粘贴用法（若其他位置仍用则保留）。
- `PromptInput.razor`：
  - 注入 `UploadedFileService`；字段 `_pasteRef`、`_uploadError`。
  - 容器加 `data-session-id="@SessionState?.SessionId"`。
  - `OnAfterRenderAsync(firstRender)`：`_pasteRef ??= DotNetObjectReference.Create(this);` 调用 `setupPasteHandler(ContainerRef.Id, _pasteRef)` 与 `setupDropZone(ContainerRef.Id, _pasteRef)`；移除旧的 `setupDropZone`/`setupPasteHandler` 调用。
  - `[JSInvokable] HandleUploadedFiles`：图片调用 `CreateImageAttachmentAsync`；普通文件调用 `CreatePathReferenceAsync`；加入 `Attachments`；`StateHasChanged()`。
  - `[JSInvokable] HandleUploadFailed`：设置 `_uploadError`，`StateHasChanged()`。
  - `IAsyncDisposable.DisposeAsync`：调用 `disposePasteHandler`/`disposeDropZone`，释放 `_pasteRef`；JS 失败静默。

- [ ] **Step 4: Run tests to verify they pass**

Run same command as Step 2.
Expected: PASS。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.WebUI/wwwroot/js/app.js samples/Seeing.Agent.WebUI/Components/PromptInput.razor tests/apps/Seeing.Agent.WebUI.Tests/Infrastructure/UploadScriptContractTests.cs
git commit -m "feat(webui): upload pasted and dropped files over http"
```

---

### Task 6: WebUI 文件选择器、路径 chip 与删除

**Files:**
- Modify: `samples/Seeing.Agent.WebUI/Components/PromptInput.razor`（`OnInputFileChange`、`OnAttachmentRemoved`、`OnClear`）
- Modify: `samples/Seeing.Agent.WebUI/Components/AttachmentPreview.razor`
- Modify: `samples/Seeing.Agent.WebUI/wwwroot/css/prompt-input.css`
- Create: `samples/Seeing.Agent.WebUI/Models/AttachmentClassifier.cs`
- Test: `tests/apps/Seeing.Agent.WebUI.Tests/Models/AttachmentClassificationTests.cs`

**Interfaces:**
- Consumes: `UploadedFileService`（Task 4）、`AttachmentDeliveryKind`（Task 3）。
- Produces:
```csharp
public static class AttachmentClassifier
{
    public static bool IsImage(string? contentType, string fileName);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class AttachmentClassificationTests
{
    [Theory]
    [InlineData("image/png", "a.bin", true)]
    [InlineData("", "a.PNG", true)]
    [InlineData("application/pdf", "a.pdf", false)]
    public void IsImage_ShouldUseContentTypeThenExtension(string type, string name, bool expected)
        => AttachmentClassifier.IsImage(type, name).Should().Be(expected);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll --TestCaseFilter:"FullyQualifiedName~AttachmentClassificationTests"
```
Expected: FAIL。

- [ ] **Step 3: Implement picker, preview, deletion**

- 新增 `samples/Seeing.Agent.WebUI/Models/AttachmentClassifier.cs`（或 Services 下，保持命名空间一致）：`contentType` 以 `image/` 开头，或扩展名属于图片集合。
- `PromptInput.OnInputFileChange`：
  - 图片：读流 ≤10 MB → data URL → `BinaryAttachment`。
  - 普通文件：读流 ≤50 MB → `UploadedFileService.StoreAsync` → `PathReference` chip（`PromptPath`、`SessionRelativePath`）。
  - 失败加 `Failed` 状态附件；不中断其他项。
- `AttachmentPreview`：
  - 路径引用显示“路径”小标记，`Tooltip Title="@currentAttachment.PromptPath"`；
  - `IsImage && Base64Content` 才渲染图片；
  - `AddAttachment` 对 `IsPathReference` 跳过 10 MB 校验。
- `PromptInput.OnAttachmentRemoved`：路径引用调用 `UploadedFileService.DeleteAsync`（best-effort）。
- `PromptInput.OnClear`：删除所有未发送的路径引用文件后清空。
- CSS：加 `.attachment-item-path` 与“路径”标记样式。

- [ ] **Step 4: Run tests to verify they pass**

Run same command as Step 2.
Expected: PASS。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.WebUI/Components/PromptInput.razor samples/Seeing.Agent.WebUI/Components/AttachmentPreview.razor samples/Seeing.Agent.WebUI/wwwroot/css/prompt-input.css samples/Seeing.Agent.WebUI/Models/AttachmentClassifier.cs tests/apps/Seeing.Agent.WebUI.Tests/Models/AttachmentClassificationTests.cs
git commit -m "feat(webui): route picked files through path references"
```

---

### Task 7: TUI 粘贴动作与输入缓冲

**Files:**
- Modify: `samples/Seeing.Agent.Tui/Input/TuiInputTypes.cs`
- Modify: `samples/Seeing.Agent.Tui/Input/InputKeyDecoder.cs`
- Modify: `samples/Seeing.Agent.Tui/Input/TuiInputEditorState.cs`
- Test: `tests/apps/Seeing.Agent.Tui.Tests/Input/InputKeyDecoderTests.cs`（追加用例）
- Test: `tests/apps/Seeing.Agent.Tui.Tests/Input/TuiInputEditorStateTests.cs`（追加用例）

**Interfaces:**
- Consumes: 无。
- Produces:
```csharp
public enum TuiInputAction
{
    // 现有值...
    PasteStart,
    PasteEnd,
    PasteFromClipboard,
}

public sealed class TuiInputEditorState
{
    public void AppendText(string text);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Decode_PasteStartEnd_ShouldMapToPasteActions()
{
    InputKeyDecoder.Decode(new TuiRawPasteStart(), false).Action.Should().Be(TuiInputAction.PasteStart);
    InputKeyDecoder.Decode(new TuiRawPasteEnd(), false).Action.Should().Be(TuiInputAction.PasteEnd);
}

[Fact]
public void Decode_CtrlV_ShouldMapToPasteFromClipboard()
    => InputKeyDecoder.Decode(new TuiRawKey(ConsoleKey.V, 'v', true, false, false), false)
        .Action.Should().Be(TuiInputAction.PasteFromClipboard);

[Fact]
public void AppendText_ShouldInsertAtCursorAndAdvance()
{
    var state = new TuiInputEditorState();
    state.SetTextAndCursor("ab", 1);
    state.AppendText("-x");
    state.Text.Should().Be("a-xb");
    state.Cursor.Should().Be(3);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.Tui.Tests
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll --TestCaseFilter:"FullyQualifiedName~InputKeyDecoderTests|FullyQualifiedName~TuiInputEditorStateTests"
```
Expected: FAIL。

- [ ] **Step 3: Implement actions and editor method**

- `InputKeyDecoder.Decode`：`TuiRawPasteStart` → `PasteStart`；`TuiRawPasteEnd` → `PasteEnd`；`DecodeKey` 在 Ctrl+C/D 分支旁加 `Ctrl && Key == ConsoleKey.V` → `PasteFromClipboard`。
- `TuiInputEditorState.AppendText`：空字符串直接返回；`Text = Text.Insert(Cursor, text); Cursor += text.Length;`。

- [ ] **Step 4: Run tests to verify they pass**

Run same command as Step 2.
Expected: PASS。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.Tui/Input/TuiInputTypes.cs samples/Seeing.Agent.Tui/Input/InputKeyDecoder.cs samples/Seeing.Agent.Tui/Input/TuiInputEditorState.cs tests/apps/Seeing.Agent.Tui.Tests/Input/InputKeyDecoderTests.cs tests/apps/Seeing.Agent.Tui.Tests/Input/TuiInputEditorStateTests.cs
git commit -m "feat(tui): add paste input actions"
```

---

### Task 8: TUI 剪贴板读取与粘贴规划

**Files:**
- Create: `samples/Seeing.Agent.Tui/Services/Clipboard/IClipboardReader.cs`
- Create: `samples/Seeing.Agent.Tui/Services/Clipboard/WindowsClipboardReader.cs`
- Create: `samples/Seeing.Agent.Tui/Services/Clipboard/UnsupportedClipboardReader.cs`
- Create: `samples/Seeing.Agent.Tui/Services/TuiPastePlanner.cs`
- Modify: `samples/Seeing.Agent.Tui/Services/TuiAttachmentResolver.cs`（公开 MIME 判定）
- Modify: `samples/Seeing.Agent.Tui/Program.cs`（注册 `IClipboardReader`）
- Test: `tests/apps/Seeing.Agent.Tui.Tests/Services/TuiPastePlannerTests.cs`
- Test: `tests/apps/Seeing.Agent.Tui.Tests/Services/TuiAttachmentResolverTests.cs`（追加用例）

**Interfaces:**
- Consumes: `PastedFileLayout`（Task 1）。
- Produces:
```csharp
public sealed record ClipboardFile(string Path, string MimeType, bool IsImage, long Size);
public sealed record ClipboardImage(byte[] PngBytes, string FileName, long Size);

public interface IClipboardReader
{
    bool IsSupported { get; }
    IReadOnlyList<ClipboardFile> ReadFiles();
    string? ReadText();
    Task<ClipboardImage?> ReadImageAsync(CancellationToken ct = default);
}

public sealed record TuiPastePlan(
    IReadOnlyList<string> ImageFilePaths,
    IReadOnlyList<string> PromptPaths,
    string? Text,
    ClipboardImage? Screenshot);

public static class TuiPastePlanner
{
    public static TuiPastePlan Plan(
        string? terminalText,
        IReadOnlyList<ClipboardFile> files,
        string? clipboardText,
        ClipboardImage? image,
        string projectRoot);
}

// TuiAttachmentResolver 追加
public static string ResolveMimeType(string path);
public static bool IsImagePath(string path);
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class TuiPastePlannerTests
{
    [Fact]
    public void Plan_Files_ShouldWinAndSplitImagesFromPaths()
    {
        var plan = TuiPastePlanner.Plan(
            terminalText: "\"C:\\a.txt\"",
            files: [
                new ClipboardFile(@"C:\a.txt", "text/plain", false, 1),
                new ClipboardFile(@"C:\b.png", "image/png", true, 2)
            ],
            clipboardText: "ignored",
            image: null,
            projectRoot: @"C:\work");

        plan.ImageFilePaths.Should().Equal(@"C:\b.png");
        plan.PromptPaths.Should().Equal("C:/a.txt");
        plan.Text.Should().BeNull();
    }

    [Fact]
    public void Plan_NoFiles_ShouldPreferTerminalTextThenClipboardText()
    {
        TuiPastePlanner.Plan("pasted", [], "clip", null, @"C:\work").Text.Should().Be("pasted");
        TuiPastePlanner.Plan(null, [], "clip", null, @"C:\work").Text.Should().Be("clip");
    }

    [Fact]
    public void Plan_NoText_ShouldUseScreenshot()
    {
        var image = new ClipboardImage([1, 2, 3], "screenshot.png", 3);
        var plan = TuiPastePlanner.Plan(null, [], null, image, @"C:\work");
        plan.Screenshot.Should().BeSameAs(image);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.Tui.Tests
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll --TestCaseFilter:"FullyQualifiedName~TuiPastePlannerTests|FullyQualifiedName~TuiAttachmentResolverTests"
```
Expected: FAIL。

- [ ] **Step 3: Implement clipboard abstraction and planner**

- `TuiPastePlanner.Plan` 按 spec §5.3.2：files 非空 → 分类并忽略文本；否则 terminalText；否则 clipboardText；否则 screenshot。
- `PromptPaths` 用 `PastedFileLayout.ToPromptPath(projectRoot, file.Path)`。
- `TuiAttachmentResolver`：把现有 `ResolveMimeType(fullPath, bytes)` 保留为私有 sniff；新增公开 `ResolveMimeType(string path)`（扩展名 + 补充 MIME，未知返回 `application/octet-stream`）与 `IsImagePath`。
- `WindowsClipboardReader`：
  - `ReadFiles`：P/Invoke `CF_HDROP` + `DragQueryFileW`；用公开 MIME 判定；返回路径/大小。
  - `ReadText`：`CF_UNICODETEXT`。
  - `ReadImageAsync`：先读注册格式 `"PNG"` 的 `HGLOBAL`；否则调用 PowerShell（`-NoProfile -STA -NonInteractive -EncodedCommand`）保存 `%TEMP%/seeing-paste-<guid>.png`；读字节后删除；超时 3 秒。
  - 所有剪贴板打开都用 `try/finally` 关闭。
- `UnsupportedClipboardReader`：`IsSupported=false`，空文件/`null` 文本/`null` 图片。
- `Program.cs`：
```csharp
services.AddSingleton<IClipboardReader>(_ =>
    OperatingSystem.IsWindows()
        ? new WindowsClipboardReader()
        : new UnsupportedClipboardReader());
```

- [ ] **Step 4: Run tests to verify they pass**

Run same command as Step 2.
Expected: PASS（Windows P/Invoke 行为由手工验收覆盖）。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.Tui/Services/Clipboard samples/Seeing.Agent.Tui/Services/TuiPastePlanner.cs samples/Seeing.Agent.Tui/Services/TuiAttachmentResolver.cs samples/Seeing.Agent.Tui/Program.cs tests/apps/Seeing.Agent.Tui.Tests/Services/TuiPastePlannerTests.cs tests/apps/Seeing.Agent.Tui.Tests/Services/TuiAttachmentResolverTests.cs
git commit -m "feat(tui): read clipboard and plan pastes"
```

---

### Task 9: TUI 引擎接线（Ctrl+V、`@path`、`/attach`）

**Files:**
- Create: `samples/Seeing.Agent.Tui/Services/TuiInlineAttachmentParser.cs`
- Modify: `samples/Seeing.Agent.Tui/Services/TuiChatEngine.cs`
- Test: `tests/apps/Seeing.Agent.Tui.Tests/Services/TuiInlineAttachmentParserTests.cs`

**Interfaces:**
- Consumes: `IClipboardReader`、`TuiPastePlanner`、`PastedFileLayout`、`TuiAttachmentResolver.IsImagePath`（Task 8）。
- Produces:
```csharp
public sealed record TuiInlineAttachmentPlan(
    string RemainingText,
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> PromptPaths);

public sealed record TuiPathAttachmentPlan(
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> PromptPaths);

public static class TuiInlineAttachmentParser
{
    public static TuiInlineAttachmentPlan ParseInline(
        TuiAttachmentResolver resolver, string text, string projectRoot);

    public static TuiPathAttachmentPlan Classify(
        TuiAttachmentResolver resolver, IEnumerable<string> paths, string projectRoot);
}
```

`TuiChatEngine` 构造函数末尾新增可选参数：
```csharp
IClipboardReader? clipboard = null
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class TuiInlineAttachmentParserTests
{
    [Fact]
    public void ParseInline_NonImage_ShouldMovePathOutOfText()
    {
        var plan = TuiInlineAttachmentParser.ParseInline(
            new TuiAttachmentResolver(),
            @"@C:\work\a.txt hello",
            @"C:\work");

        plan.RemainingText.Should().Be("hello");
        plan.PromptPaths.Should().Equal("a.txt");
        plan.ImagePaths.Should().BeEmpty();
    }

    [Fact]
    public void ParseInline_Image_ShouldKeepItAsImagePath()
    {
        var plan = TuiInlineAttachmentParser.ParseInline(
            new TuiAttachmentResolver(),
            @"@C:\work\photo.png hi",
            @"C:\work");

        plan.RemainingText.Should().Be("hi");
        plan.ImagePaths.Should().Equal(@"C:\work\photo.png");
        plan.PromptPaths.Should().BeEmpty();
    }

    [Fact]
    public void Classify_MixedPaths_ShouldSplitImageAndPromptPaths()
    {
        var plan = TuiInlineAttachmentParser.Classify(
            new TuiAttachmentResolver(),
            [@"C:\work\photo.png", @"C:\work\a.txt"],
            @"C:\work");

        plan.ImagePaths.Should().Equal(@"C:\work\photo.png");
        plan.PromptPaths.Should().Equal("a.txt");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```powershell
dotnet build tests/apps/Seeing.Agent.Tui.Tests
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll --TestCaseFilter:"FullyQualifiedName~TuiInlineAttachmentParserTests"
```
Expected: FAIL（`TuiInlineAttachmentParser` 不存在）。

- [ ] **Step 3: Implement parser and wire the engine**

- `TuiInlineAttachmentParser`：
  - `ParseInline`：`resolver.TryParseInline`；无附件返回原文本/空列表；否则按 `IsImagePath` 分类；非图片 `PromptPath = PastedFileLayout.ToPromptPath(projectRoot, Path.GetFullPath(path))`；`RemainingText` 用解析结果。
  - `Classify`：对每个路径 `IsImagePath` 分类；非图片同样生成 prompt 路径。
- `TuiChatEngine` 字段：
```csharp
private readonly IClipboardReader _clipboard;
private readonly StringBuilder _pasteBuffer = new();
private bool _pasteActive;
```
- 构造函数末尾加 `IClipboardReader? clipboard = null`；赋值 `_clipboard = clipboard ?? new UnsupportedClipboardReader();`。
- `HandleInputAsync` 在 completion 路由之前处理：
  - `PasteStart`：清空 `_pasteBuffer`，`_pasteActive = true`，`_editor.SetPasteActive(true)`，返回 false。
  - `_pasteActive` 且 `InsertText`/`InsertNewline`：追加到 `_pasteBuffer`（换行用 `\n`），返回 false。
  - `PasteEnd`：`_pasteActive = false`，`_editor.SetPasteActive(false)`，`await HandleClipboardPasteAsync(_pasteBuffer.ToString(), ct)`，返回 false。
  - `PasteFromClipboard`：`await HandleClipboardPasteAsync(null, ct)`，返回 false。
  - `Cancel` 时清空 `_pasteBuffer`/`_pasteActive`。
- `HandleClipboardPasteAsync(string? terminalText, CancellationToken ct)`：
  - `ForceRender()`；
  - `var files = _clipboard.ReadFiles();`
  - 仅当 files 为空且 terminalText 为空时 `var clipboardText = _clipboard.ReadText();`，仍为空才 `var image = await _clipboard.ReadImageAsync(ct);`
  - `var plan = TuiPastePlanner.Plan(terminalText, files, clipboardText, image, ResolveWorkspaceRoot(_workspace));`
  - `foreach (var path in plan.ImageFilePaths) await StageAttachmentAsync(path, ct);`
  - `if (plan.Screenshot is { } shot) await StageAttachmentAsync(new TuiAttachment(screenshotName, "image/png", Convert.ToBase64String(shot.PngBytes), shot.Size), ct);`，`screenshotName` 用 `PastedFileLayout.BuildStoredFileName("screenshot.png", DateTimeOffset.Now, suffix)`。
  - `if (!string.IsNullOrEmpty(plan.Text)) _editor.AppendText(plan.Text);`
  - `if (plan.PromptPaths.Count > 0) _editor.AppendText(PastedFileLayout.FormatPromptBlock(plan.PromptPaths) + "\n" ...);`
  - 提示：图片“已粘贴图片”、路径“已插入文件路径”。
- `SubmitAsync` 中 `@path`：
  - `var inline = TuiInlineAttachmentParser.ParseInline(_attachmentResolver, text, ResolveWorkspaceRoot(_workspace));`
  - `foreach (var path in inline.ImagePaths) await StageAttachmentAsync(path, ct);`
  - `remaining = PastedFileLayout.AppendPromptPaths(inline.RemainingText, inline.PromptPaths);`
- `HandleAttachAsync`：
  - `var plan = TuiInlineAttachmentParser.Classify(_attachmentResolver, paths, ResolveWorkspaceRoot(_workspace));`
  - 图片 `StageAttachmentAsync`；非图片 `_editor.AppendText(block)`，提示“已插入文件路径”。
- `StageAttachmentAsync(TuiAttachment attachment, CancellationToken ct)` 重载：加入 `_pendingAttachments` 与 `_editor.AddAttachment`；原路径版本内部调用 `LoadAsync` 后复用该重载。
- `BuildChatInput` 不变：`_pendingAttachments` 只会有图片。

- [ ] **Step 4: Run tests to verify they pass**

Run same command as Step 2.
Expected: PASS；`dotnet build samples/Seeing.Agent.Tui` 0 error。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.Tui/Services/TuiInlineAttachmentParser.cs samples/Seeing.Agent.Tui/Services/TuiChatEngine.cs tests/apps/Seeing.Agent.Tui.Tests/Services/TuiInlineAttachmentParserTests.cs
git commit -m "feat(tui): wire ctrl-v and unify attachment paths"
```

---

### Task 10: 文档、验收与全量回归

**Files:**
- Modify: `samples/Seeing.Agent.Tui/README.md`（键盘操作、附件、限制）
- Modify: `docs/architecture/README.md` 或相关条目（若需要登记上传目录约定）
- Test: 手工验收（见 spec §6.2）+ 全量构建/测试

**Interfaces:**
- Consumes: 前 9 个任务。
- Produces: 无。

- [ ] **Step 1: 更新 TUI README**

- 键盘表加 Ctrl+V：文本/文件/截图行为。
- 附件节说明 `@path` / `/attach`：图片进附件、非图片进路径；`/detach` 只管图片。
- 当前限制：WT 1.25+ kitty、非 Windows 图片、PDF/Office 不提取正文。

- [ ] **Step 2: 手工验收 WebUI**

按 spec §6.2 执行本机与远程：
- >32 KB 截图 Ctrl+V → 附件；
- 普通文件粘贴/拖拽/选择器 → 路径 chip；
- 多文件；删除 chip；
- 纯文本粘贴不产生附件。

- [ ] **Step 3: 手工验收 TUI（Windows Terminal 1.24）**

按 spec §6.2：
- 截图 Ctrl+V → 附件；
- 普通文件 Ctrl+V → 路径；
- 图片文件 Ctrl+V → 附件；
- 多文件；文本；
- `@path` / `/attach` 分类；`/detach` 不影响路径文本。

- [ ] **Step 4: 全量构建与回归**

```powershell
dotnet build Seeing.Agent.slnx
dotnet build tests/support/Seeing.Agent.Tools.Support.Tests
dotnet vstest tests/support/Seeing.Agent.Tools.Support.Tests/bin/Debug/net10.0/Seeing.Agent.Tools.Support.Tests.dll
dotnet build tests/capabilities/Seeing.Agent.Files.Tests
dotnet vstest tests/capabilities/Seeing.Agent.Files.Tests/bin/Debug/net10.0/Seeing.Agent.Files.Tests.dll
dotnet build tests/apps/Seeing.Agent.WebUI.Tests
dotnet vstest tests/apps/Seeing.Agent.WebUI.Tests/bin/Debug/net10.0/Seeing.Agent.WebUI.Tests.dll
dotnet build tests/apps/Seeing.Agent.Tui.Tests
dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll
```
Expected: 0 error；目标测试全绿。

- [ ] **Step 5: Commit**

```powershell
git add samples/Seeing.Agent.Tui/README.md docs/architecture/README.md
git commit -m "docs: document unified file input behavior"
```

---

## Execution Handoff

计划保存后，请按 spec 与 plan 的接口对照选择执行方式。若进入实施，优先完成 Task 1-2（共享底座），再按 Web/TUI 顺序推进；Web 与 TUI 在 Task 6 之后可并行。
