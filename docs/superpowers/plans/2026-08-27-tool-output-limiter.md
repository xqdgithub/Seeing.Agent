# 工具输出统一限制（Tool Output Limiter）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在装饰器层统一限制工具输出——超限输出落盘到会话 ref 目录（`{sessionDir}/{sessionId}.ref/`），替换为头+尾预览，保证完整数据可恢复且不直接注入 LLM。

**Architecture:** 新增 `ToolOutputLimiterDecorator` 作为装饰器链最外层兜底（最后注册），所有工具（内置/第三方/MCP）统一覆盖。超限时经 `IToolOutputStore` 将全文写入会话 ref 目录，`Output` 替换为 `<persisted-output>` 头+尾预览，Metadata 记录路径与统计，白名单放行 ref 目录使 agent 可 read 恢复。原语层 `IRelocatableSessionStore` 暴露 `BaseDirectory` 供 ref 目录解析跟随会话存储重定位。

**Tech Stack:** C# / .NET 10、xUnit 2.9 + FluentAssertions + Moq、IOptionsMonitor 热重载、ToolDecorator 装饰器链、ToolCapability 能力声明。

---

## 关键背景（实现者必读）

1. **装饰器链顺序**：`ToolDecoratorRegistry.Apply`（`src/Seeing.Agent/Decorators/ToolDecoratorRegistry.cs:103-108`）为 `current = factory(current)`，**后注册者在外层**。现有注册顺序 Retry→Timeout→Cache（`ServiceCollectionExtensions.cs:507-528`），故实际从外到内为 **Cache→Timeout→Retry→工具**——`:507/:520` 的"最外层重试/最内层缓存"注释与实现相反，**勿信注释**。Limiter 必须在 `Cache` 之后注册才成为最外层。
2. **测试命令**（MTP 不可用，一律 VSTest）：先 `dotnet build` 目标测试项目，再 `dotnet vstest ...dll --TestCaseFilter:"FullyQualifiedName~关键字"`。
3. **能力读取**：`ToolDecorator.Capabilities` 透传内层工具能力（`ToolDecorator.cs:41`），装饰器内经 `Capabilities.TryGetValue(...)` 读取。
4. **配置热重载**：与 `ToolTimeoutDecorator` 一致，构造注入 `IOptionsMonitor<SeeingAgentOptions>`，运行时读 `CurrentValue`。

---

### Task 1: 新增能力键 + 输出限制配置类

**Files:**
- Modify: `src/Seeing.Agent.Abstractions/Tools/ToolCapabilityKeys.cs`
- Modify: `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs`
- Test: `tests/Seeing.Agent.Tests/Configuration/ToolOutputOptionsTests.cs`

- [ ] **Step 1: 写失败测试（配置默认值）**

Create `tests/Seeing.Agent.Tests/Configuration/ToolOutputOptionsTests.cs`:

```csharp
using FluentAssertions;
using Seeing.Agent.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class ToolOutputOptionsTests
{
    [Fact]
    public void Defaults_ShouldBeSane()
    {
        var options = new SeeingAgentOptions().ToolOutput;

        options.Enabled.Should().BeTrue();
        options.MaxInlineBytes.Should().Be(50 * 1024);
        options.PreviewHeadChars.Should().Be(1024);
        options.PreviewTailChars.Should().Be(1024);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolOutputOptionsTests"
```

Expected: FAIL（`ToolOutput` 属性不存在，编译错误）。

- [ ] **Step 3: 实现能力键与配置类**

在 `src/Seeing.Agent.Abstractions/Tools/ToolCapabilityKeys.cs` 的预留键区块前追加：

```csharp
        // === 输出限制能力键（ToolOutputLimiterDecorator 消费）===

        /// <summary>豁免工具输出限制（"true"/"false"，默认 false）。工具自身已控制输出（如内置分页工具）时声明。</summary>
        public const string OutputSkip = "output.skip";

        /// <summary>工具输出落盘阈值上限（字节）。存在时覆盖全局 ToolOutput.MaxInlineBytes。</summary>
        public const string OutputMaxBytes = "output.maxBytes";
```

在 `src/Seeing.Agent/Configuration/SeeingAgentOptions.cs` 中 `ToolExecutionTimeout` 属性后追加属性：

```csharp
        /// <summary>工具输出限制配置（超限落盘到会话 ref 目录 + 头尾预览）</summary>
        public ToolOutputOptions ToolOutput { get; set; } = new();
```

在 `SeeingAgentOptions.cs` 文件末尾（`TitleGenerationOptions` 类之后）追加类：

```csharp
    /// <summary>
    /// 工具输出限制选项
    /// <para>工具输出超过 <see cref="MaxInlineBytes"/> 时：全文落盘到会话 ref 目录，
    /// Output 替换为 &lt;persisted-output&gt; 头+尾预览，完整内容可经 read 工具读取。</para>
    /// </summary>
    public sealed class ToolOutputOptions
    {
        /// <summary>是否启用输出限制（默认 true）</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>内联上限（UTF8 字节）。输出超过此值触发落盘+预览。对齐 FileSystemHelper.MaxBytes。</summary>
        public int MaxInlineBytes { get; set; } = 50 * 1024;

        /// <summary>预览头部字符数</summary>
        public int PreviewHeadChars { get; set; } = 1024;

        /// <summary>预览尾部字符数</summary>
        public int PreviewTailChars { get; set; } = 1024;
    }
```

- [ ] **Step 4: 运行测试验证通过**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolOutputOptionsTests"
```

Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent.Abstractions/Tools/ToolCapabilityKeys.cs src/Seeing.Agent/Configuration/SeeingAgentOptions.cs tests/Seeing.Agent.Tests/Configuration/ToolOutputOptionsTests.cs
git commit -m "feat(output-limiter): 新增输出限制能力键与 ToolOutputOptions 配置"
```

---

### Task 2: IRelocatableSessionStore 暴露 BaseDirectory

**Files:**
- Modify: `src/Seeing.Session/Storage/IRelocatableSessionStore.cs`
- Modify: `src/Seeing.Session/Storage/FileSessionStore.cs`
- Test: `tests/Seeing.Session.Tests/Storage/FileSessionStoreBaseDirectoryTests.cs`

- [ ] **Step 1: 写失败测试**

Create `tests/Seeing.Session.Tests/Storage/FileSessionStoreBaseDirectoryTests.cs`:

```csharp
using FluentAssertions;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class FileSessionStoreBaseDirectoryTests
{
    [Fact]
    public void BaseDirectory_ShouldReturnConstructedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-base-dir-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(tempDir);
            store.BaseDirectory.Should().Be(tempDir);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BaseDirectory_ShouldUpdateAfterRelocation()
    {
        var tempA = Path.Combine(Path.GetTempPath(), "seeing-base-a-" + Guid.NewGuid().ToString("N"));
        var tempB = Path.Combine(Path.GetTempPath(), "seeing-base-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(tempA);
            store.SetBaseDirectory(tempB);
            store.BaseDirectory.Should().Be(tempB);
        }
        finally
        {
            foreach (var dir in new[] { tempA, tempB })
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Session.Tests
dotnet vstest tests/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileSessionStoreBaseDirectoryTests"
```

Expected: FAIL（`IRelocatableSessionStore` 无 `BaseDirectory`，编译错误）。

- [ ] **Step 3: 实现属性**

`src/Seeing.Session/Storage/IRelocatableSessionStore.cs`:

```csharp
    public interface IRelocatableSessionStore
    {
        /// <summary>当前会话存储基础目录（可空：内存实现可能无目录）</summary>
        string? BaseDirectory { get; }

        void SetBaseDirectory(string baseDirectory);
    }
```

`src/Seeing.Session/Storage/FileSessionStore.cs` 的 `SetBaseDirectory` 方法上方追加：

```csharp
        /// <summary>当前会话存储基础目录</summary>
        public string? BaseDirectory => _baseDirectory;
```

同时将 `GetDefaultSessionDirectory` 从 `private static` 改为 `public static`（供 `SessionToolOutputStore` fallback 复用）：

```csharp
        /// <summary>
        /// 获取默认会话目录路径 (~/.seeing/sessions)
        /// </summary>
        public static string GetDefaultSessionDirectory()
```

- [ ] **Step 4: 运行测试验证通过**

```bash
dotnet build tests/Seeing.Session.Tests
dotnet vstest tests/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileSessionStoreBaseDirectoryTests"
```

Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Session/Storage/IRelocatableSessionStore.cs src/Seeing.Session/Storage/FileSessionStore.cs tests/Seeing.Session.Tests/Storage/FileSessionStoreBaseDirectoryTests.cs
git commit -m "feat(output-limiter): IRelocatableSessionStore 暴露 BaseDirectory 供 ref 目录解析"
```

---

### Task 3: FileSessionStore.DeleteAsync 联动清理 ref 目录

**Files:**
- Modify: `src/Seeing.Session/Storage/FileSessionStore.cs`
- Test: `tests/Seeing.Session.Tests/Storage/FileSessionStoreRefCleanupTests.cs`

- [ ] **Step 1: 写失败测试**

Create `tests/Seeing.Session.Tests/Storage/FileSessionStoreRefCleanupTests.cs`:

```csharp
using FluentAssertions;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class FileSessionStoreRefCleanupTests
{
    private static string NewSessionId() => "ses_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRefDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-ref-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(tempDir);
            var sessionId = NewSessionId();
            await store.SaveAsync(new Seeing.Session.Core.SessionData { Id = sessionId });

            var refDir = Path.Combine(tempDir, sessionId + ".ref");
            Directory.CreateDirectory(refDir);
            File.WriteAllText(Path.Combine(refDir, "abc.txt"), "data");
            refDir.Should().Exist();

            await store.DeleteAsync(sessionId);

            refDir.Should().NotExist();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Session.Tests
dotnet vstest tests/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileSessionStoreRefCleanupTests"
```

Expected: FAIL（ref 目录未被删除）。

- [ ] **Step 3: 实现清理逻辑**

`src/Seeing.Session/Storage/FileSessionStore.cs` 中追加私有常量与方法，并在 `DeleteAsync` 开头调用：

在类字段区（`LockTimeout` 附近）追加：

```csharp
        /// <summary>会话引用资源目录后缀（工具输出落盘目录约定）</summary>
        private const string RefDirectorySuffix = ".ref";
```

`DeleteAsync` 方法体中 `ValidateSessionId(sessionId);` 之后追加清理调用：

```csharp
            DeleteRefDirectory(sessionId);
```

在 `DeleteAsync` 方法后追加私有方法：

```csharp
        /// <summary>
        /// 删除会话引用资源目录（{sessionId}.ref/），用于工具输出落盘等附属资源。
        /// 目录不存在或删除失败时仅记录日志，不影响主流程。
        /// </summary>
        private void DeleteRefDirectory(string sessionId)
        {
            var refDir = Path.Combine(_baseDirectory, sessionId + RefDirectorySuffix);
            try
            {
                if (Directory.Exists(refDir))
                {
                    Directory.Delete(refDir, recursive: true);
                    _logger?.LogDebug("删除会话引用目录成功: {RefDir}", refDir);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "删除会话引用目录失败: {SessionId}, {RefDir}", sessionId, refDir);
            }
        }
```

- [ ] **Step 4: 运行测试验证通过**

```bash
dotnet build tests/Seeing.Session.Tests
dotnet vstest tests/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll --TestCaseFilter:"FullyQualifiedName~FileSessionStoreRefCleanupTests"
```

Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Session/Storage/FileSessionStore.cs tests/Seeing.Session.Tests/Storage/FileSessionStoreRefCleanupTests.cs
git commit -m "feat(output-limiter): 删除会话时联动清理 {sessionId}.ref 目录"
```

---

### Task 4: IToolOutputStore + SessionToolOutputStore

**Files:**
- Create: `src/Seeing.Agent/Output/IToolOutputStore.cs`
- Create: `src/Seeing.Agent/Output/SessionToolOutputStore.cs`
- Test: `tests/Seeing.Agent.Tests/Output/SessionToolOutputStoreTests.cs`

- [ ] **Step 1: 写失败测试**

Create `tests/Seeing.Agent.Tests/Output/SessionToolOutputStoreTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Output;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Output;

public class SessionToolOutputStoreTests
{
    private static (string TempDir, FileSessionStore Store) CreateTempStore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-output-store-" + Guid.NewGuid().ToString("N"));
        return (tempDir, new FileSessionStore(tempDir));
    }

    [Fact]
    public async Task SaveAsync_ShouldWriteFullContentAndReturnPath()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            var path = await outputStore.SaveAsync("ses_a", "call_1", "hello\nworld", CancellationToken.None);

            path.Should().Be(Path.Combine(tempDir, "ses_a.ref", "call_1.txt"));
            File.ReadAllText(path).Should().Be("hello\nworld");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ShouldSanitizeIllegalFileNameChars()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            var path = await outputStore.SaveAsync("ses_a", "bad/name:1", "data", CancellationToken.None);

            path.Should().NotContain("/").And.NotContain(":");
            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Be("data");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetRefDirectory_ShouldUseStoreBaseDirectory()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            outputStore.GetRefDirectory("ses_a").Should().Be(Path.Combine(tempDir, "ses_a.ref"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DeleteSessionRefDirectory_ShouldRemoveDirectory()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            var refDir = outputStore.GetRefDirectory("ses_a");
            Directory.CreateDirectory(refDir);
            File.WriteAllText(Path.Combine(refDir, "x.txt"), "x");
            refDir.Should().Exist();

            outputStore.DeleteSessionRefDirectory("ses_a");

            refDir.Should().NotExist();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionToolOutputStoreTests"
```

Expected: FAIL（`Seeing.Agent.Output` 命名空间不存在）。

- [ ] **Step 3: 实现接口与存储**

Create `src/Seeing.Agent/Output/IToolOutputStore.cs`:

```csharp
namespace Seeing.Agent.Output;

/// <summary>
/// 工具输出存储 - 超长工具输出的落盘服务。
/// <para>完整内容写入会话 ref 目录（{会话存储目录}/{sessionId}.ref/），随会话保留、可关联、不丢失。</para>
/// </summary>
public interface IToolOutputStore
{
    /// <summary>解析会话 ref 目录（不创建；路径 = 会话存储目录/{sessionId}.ref）</summary>
    string GetRefDirectory(string sessionId);

    /// <summary>保存工具输出全文，返回文件路径（文件名 = 净化后的 callId.txt，5 秒内未完成抛 TimeoutException）</summary>
    Task<string> SaveAsync(string sessionId, string callId, string content, CancellationToken cancellationToken = default);

    /// <summary>删除会话 ref 目录（删除失败仅记录，不抛异常）</summary>
    void DeleteSessionRefDirectory(string sessionId);
}
```

Create `src/Seeing.Agent/Output/SessionToolOutputStore.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Seeing.Session.Storage;

namespace Seeing.Agent.Output;

/// <summary>
/// 基于会话存储目录的工具输出落盘实现。
/// <para>ref 目录解析跟随 IRelocatableSessionStore.BaseDirectory（工作区切换自动重定位），
/// 无法解析时回退到 FileSessionStore 默认目录（~/.seeing/sessions）。</para>
/// </summary>
public sealed class SessionToolOutputStore : IToolOutputStore
{
    private const string RefDirectorySuffix = ".ref";
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(5);

    private readonly ISessionStore _store;
    private readonly ILogger<SessionToolOutputStore>? _logger;

    public SessionToolOutputStore(ISessionStore store, ILogger<SessionToolOutputStore>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <inheritdoc />
    public string GetRefDirectory(string sessionId)
    {
        var sessionDir = (_store as IRelocatableSessionStore)?.BaseDirectory
                         ?? FileSessionStore.GetDefaultSessionDirectory();
        return Path.Combine(sessionDir, SanitizeName(sessionId) + RefDirectorySuffix);
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(string sessionId, string callId, string content, CancellationToken cancellationToken = default)
    {
        var refDir = GetRefDirectory(sessionId);
        Directory.CreateDirectory(refDir);

        var fileName = SanitizeName(callId) + ".txt";
        var filePath = Path.Combine(refDir, fileName);

        var writeTask = File.WriteAllTextAsync(filePath, content, System.Text.Encoding.UTF8, cancellationToken);
        var timeoutTask = Task.Delay(SaveTimeout, CancellationToken.None);
        var completed = await Task.WhenAny(writeTask, timeoutTask).ConfigureAwait(false);
        if (!ReferenceEquals(completed, writeTask))
        {
            _logger?.LogWarning("[SessionToolOutputStore] 落盘超时，放弃写入: {FilePath}", filePath);
            throw new TimeoutException($"工具输出落盘超时: {filePath}");
        }
        await writeTask.ConfigureAwait(false);

        _logger?.LogDebug("工具输出已落盘: {FilePath} ({Bytes} 字节)", filePath, content.Length);
        return filePath;
    }

    /// <inheritdoc />
    public void DeleteSessionRefDirectory(string sessionId)
    {
        try
        {
            var refDir = GetRefDirectory(sessionId);
            if (Directory.Exists(refDir))
            {
                Directory.Delete(refDir, recursive: true);
                _logger?.LogDebug("已删除会话引用目录: {RefDir}", refDir);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "删除会话引用目录失败: {SessionId}", sessionId);
        }
    }

    /// <summary>净化文件名非法字符，空结果回退 GUID。</summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Guid.NewGuid().ToString("N");

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }
}
```

- [ ] **Step 4: 运行测试验证通过**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~SessionToolOutputStoreTests"
```

Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Output/ tests/Seeing.Agent.Tests/Output/SessionToolOutputStoreTests.cs
git commit -m "feat(output-limiter): IToolOutputStore 落盘服务与会话 ref 目录解析"
```

---

### Task 5: ToolOutputLimiterDecorator

**Files:**
- Create: `src/Seeing.Agent/Decorators/ToolOutputLimiterDecorator.cs`
- Test: `tests/Seeing.Agent.Tests/Decorators/ToolOutputLimiterDecoratorTests.cs`

- [ ] **Step 1: 写失败测试**

Create `tests/Seeing.Agent.Tests/Decorators/ToolOutputLimiterDecoratorTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Decorators;
using Seeing.Agent.Output;
using Seeing.Session.Storage;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Seeing.Agent.Tests.Decorators;

/// <summary>
/// ToolOutputLimiterDecorator 测试 - 统一工具输出限制（超限落盘 + 头尾预览）
/// </summary>
public class ToolOutputLimiterDecoratorTests
{
    private sealed class OutputTool : ITool
    {
        public string Id => "output";
        public string Description => "输出工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public string Result { get; set; } = "";
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true, Output = Result });
    }

    private sealed class SkipTool : OutputTool
    {
    }

    [ToolCapability(ToolCapabilityKeys.OutputSkip, "true")]
    private sealed class DeclaredSkipTool : OutputTool
    {
    }

    [ToolCapability(ToolCapabilityKeys.OutputMaxBytes, "100")]
    private sealed class MaxBytesTool : OutputTool
    {
    }

    private sealed class FailingTool : ITool
    {
        public string Id => "fail";
        public string Description => "失败工具";
        public IReadOnlyList<string> Tags => Array.Empty<string>();
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = false, Error = "boom" });
    }

    private static (string TempDir, ToolOutputLimiterDecorator Decorator, SessionWorkspaceWhitelist Whitelist) Create(
        ITool inner, Action<ToolOutputOptions>? configure = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-limiter-" + Guid.NewGuid().ToString("N"));
        var store = new SessionToolOutputStore(new FileSessionStore(tempDir), NullLogger<SessionToolOutputStore>.Instance);
        var whitelist = new SessionWorkspaceWhitelist();

        var opts = new SeeingAgentOptions();
        configure?.Invoke(opts.ToolOutput);
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(opts);

        var decorator = new ToolOutputLimiterDecorator(
            inner,
            options.Object,
            store,
            whitelist,
            NullLogger<ToolOutputLimiterDecorator>.Instance);
        return (tempDir, decorator, whitelist);
    }

    private static ToolContext Ctx(string sessionId = "ses_a", string? callId = "call_1")
        => new() { SessionId = sessionId, CallId = callId, CancellationToken = CancellationToken.None };

    private static async Task<ToolResult> ExecAsync(ITool decorator)
        => await decorator.ExecuteAsync(JsonDocument.Parse("{}").RootElement, Ctx());

    [Fact]
    public async Task SmallOutput_ShouldPassThroughUnchanged()
    {
        var (_, decorator, _) = Create(new OutputTool { Result = "short" });
        var result = await ExecAsync(decorator);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("short");
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task LargeOutput_ShouldSpillToRefDirectory_AndReplaceOutputWithPreview()
    {
        var big = new string('x', 60 * 1024);
        var (tempDir, decorator, whitelist) = Create(new OutputTool { Result = big });
        var result = await ExecAsync(decorator);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("<persisted-output>");
        result.Output.Should().Contain("call_1.txt");
        result.Output.Should().Contain("原始");
        result.Metadata["truncated"].Should().Be(true);
        result.Metadata["outputPath"].Should().Be(Path.Combine(tempDir, "ses_a.ref", "call_1.txt"));
        ((int)result.Metadata["originalBytes"]).Should().BeGreaterThan(50 * 1024);

        // 完整内容已落盘且可读
        File.ReadAllText((string)result.Metadata["outputPath"]).Should().Be(big);
        // 白名单已放行 ref 目录
        whitelist.Contains("ses_a", Path.Combine(tempDir, "ses_a.ref", "call_1.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task LargeOutput_ShouldKeepHeadAndTail()
    {
        var head = new string('A', 1024);
        var middle = new string('M', 5000);
        var tail = new string('Z', 1024);
        var (_, decorator, _) = Create(new OutputTool { Result = head + middle + tail });

        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("省略 5000 字符");
        result.Output.Should().Contain(head);
        result.Output.Should().Contain(tail);
    }

    [Fact]
    public async Task OutputSkipTool_ShouldBypassLimiter()
    {
        var big = new string('x', 60 * 1024);
        var (_, decorator, _) = Create(new DeclaredSkipTool { Result = big });
        var result = await ExecAsync(decorator);

        result.Output.Should().Be(big);
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task OutputMaxBytesTool_ShouldUseToolSpecificThreshold()
    {
        // 200 字节 > 能力阈值 100 → 应触发落盘；若能力不生效则 200 < 全局 50KB 不会触发
        var (_, decorator, _) = Create(new MaxBytesTool { Result = new string('x', 200) });
        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("<persisted-output>");

        // 反向验证：无能力声明时 200 字节 < 50KB，不触发
        var (_, decorator2, _) = Create(new OutputTool { Result = new string('y', 200) });
        var r2 = await ExecAsync(decorator2);
        r2.Output.Should().Be(new string('y', 200));
        r2.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task SpillFailure_ShouldDegradeToInlineTruncated()
    {
        var big = new string('x', 60 * 1024);
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-limiter-fail-" + Guid.NewGuid().ToString("N"));
        var failingStore = new Mock<IToolOutputStore>();
        failingStore.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("磁盘满"));
        var whitelist = new SessionWorkspaceWhitelist();
        var opts = new SeeingAgentOptions();
        var options = new Mock<IOptionsMonitor<SeeingAgentOptions>>();
        options.Setup(o => o.CurrentValue).Returns(opts);
        var decorator = new ToolOutputLimiterDecorator(
            new OutputTool { Result = big },
            options.Object,
            failingStore.Object,
            whitelist,
            NullLogger<ToolOutputLimiterDecorator>.Instance);

        var result = await ExecAsync(decorator);

        result.Output.Should().Contain("<persisted-output>");
        result.Output.Should().Contain("写入会话目录失败");
        result.Metadata["truncated"].Should().Be(true);
        result.Metadata["spillFailed"].Should().Be(true);
    }

    [Fact]
    public async Task FailingResult_ShouldNotBeTouched()
    {
        var (_, decorator, _) = Create(new FailingTool());
        var result = await ExecAsync(decorator);

        result.Success.Should().BeFalse();
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task EmptyOutput_ShouldNotBeTouched()
    {
        var (_, decorator, _) = Create(new OutputTool { Result = "" });
        var result = await ExecAsync(decorator);

        result.Output.Should().Be("");
        result.Metadata.Should().NotContainKey("truncated");
    }

    [Fact]
    public async Task ExactThreshold_ShouldNotSpill()
    {
        var exactly = new string('e', 50 * 1024);
        var (_, decorator, _) = Create(new OutputTool { Result = exactly });
        var result = await ExecAsync(decorator);

        result.Output.Should().Be(exactly);
        result.Metadata.Should().NotContainKey("truncated");
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolOutputLimiterDecoratorTests"
```

Expected: FAIL（`ToolOutputLimiterDecorator` 不存在）。

- [ ] **Step 3: 实现装饰器**

Create `src/Seeing.Agent/Decorators/ToolOutputLimiterDecorator.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Permission;
using Seeing.Agent.Output;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Decorators;

/// <summary>
/// 工具输出限制装饰器 - 装饰器链最外层兜底，统一限制所有工具输出长度。
/// <para>
/// 输出超过 ToolOutput.MaxInlineBytes（或工具声明 output.maxBytes）时：
/// 全文落盘到会话 ref 目录（IToolOutputStore），Output 替换为 &lt;persisted-output&gt;
/// 头+尾预览，Metadata 记录路径与统计；ref 目录加入会话白名单，agent 可经 read 恢复全文。
/// 工具声明 output.skip=true 时豁免（自身已控制输出，如内置分页工具）。
/// </para>
/// </summary>
public sealed class ToolOutputLimiterDecorator : ToolDecorator
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _options;
    private readonly IToolOutputStore _outputStore;
    private readonly IWorkspaceWhitelist _whitelist;
    private readonly ILogger<ToolOutputLimiterDecorator> _logger;

    public ToolOutputLimiterDecorator(
        ITool inner,
        IOptionsMonitor<SeeingAgentOptions> options,
        IToolOutputStore outputStore,
        IWorkspaceWhitelist whitelist,
        ILogger<ToolOutputLimiterDecorator> logger)
        : base(inner)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _outputStore = outputStore ?? throw new ArgumentNullException(nameof(outputStore));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var result = await base.ExecuteAsync(arguments, context).ConfigureAwait(false);

        var config = _options.CurrentValue.ToolOutput;
        if (!config.Enabled || !result.Success || string.IsNullOrEmpty(result.Output))
            return result;

        // 能力判断（优先于配置）
        if (Capabilities != null)
        {
            if (Capabilities.TryGetValue(ToolCapabilityKeys.OutputSkip, out var skip) &&
                string.Equals(skip, "true", StringComparison.OrdinalIgnoreCase))
                return result;

            if (Capabilities.TryGetValue(ToolCapabilityKeys.OutputMaxBytes, out var maxBytesStr) &&
                int.TryParse(maxBytesStr, out var maxBytes) && maxBytes > 0)
                config = new ToolOutputOptions { MaxInlineBytes = maxBytes, Enabled = true };
        }

        var bytes = Encoding.UTF8.GetByteCount(result.Output);
        if (bytes <= config.MaxInlineBytes)
            return result;

        var lines = result.Output.Count(c => c == '\n') + 1;
        var sessionId = context.SessionId ?? string.Empty;
        var callId = context.CallId ?? Guid.NewGuid().ToString("N");

        try
        {
            var outputPath = await _outputStore.SaveAsync(sessionId, callId, result.Output, CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(sessionId))
                _whitelist.Add(sessionId, _outputStore.GetRefDirectory(sessionId));

            result.Output = BuildPreview(result.Output, outputPath, config, bytes, lines, spillFailed: false);
            result.Metadata["truncated"] = true;
            result.Metadata["outputPath"] = outputPath;
            result.Metadata["originalBytes"] = bytes;
            result.Metadata["originalLines"] = lines;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ToolOutputLimiter] 落盘失败，降级为 inline 截断: {ToolId}", Inner.Id);
            result.Output = BuildPreview(result.Output, outputPath: null, config, bytes, lines, spillFailed: true);
            result.Metadata["truncated"] = true;
            result.Metadata["spillFailed"] = true;
        }

        return result;
    }

    /// <summary>构建 &lt;persisted-output&gt; 头+尾预览模板。</summary>
    private static string BuildPreview(
        string output,
        string? outputPath,
        ToolOutputOptions config,
        int bytes,
        int lines,
        bool spillFailed)
    {
        var headLen = Math.Max(0, config.PreviewHeadChars);
        var tailLen = Math.Max(0, config.PreviewTailChars);
        var useEllipsis = output.Length > headLen + tailLen;

        var head = useEllipsis ? output.Substring(0, headLen) : output;
        var tail = useEllipsis ? output.Substring(output.Length - tailLen) : string.Empty;
        var omitted = useEllipsis ? output.Length - headLen - tailLen : 0;

        var sb = new StringBuilder();
        sb.AppendLine("<persisted-output>");
        if (spillFailed)
        {
            sb.AppendLine($"输出过长：原始 {bytes / 1024.0:0.0}KB（{lines} 行），写入会话目录失败，仅展示部分内容。");
        }
        else
        {
            sb.AppendLine($"输出过长：原始 {bytes / 1024.0:0.0}KB（{lines} 行），完整输出已保存到：");
            sb.AppendLine($"  {outputPath}");
            sb.AppendLine("需要完整内容时，请使用 read 工具读取该文件。");
        }
        sb.AppendLine();
        if (useEllipsis)
        {
            sb.AppendLine($"预览（头部 {headLen} + 尾部 {tailLen} 字符，中间省略 {omitted} 字符）：");
            sb.AppendLine(head);
            sb.AppendLine($"...（省略 {omitted} 字符）...");
            sb.AppendLine(tail);
        }
        else
        {
            sb.AppendLine("完整内容（未省略）：");
            sb.AppendLine(output);
        }
        sb.AppendLine("</persisted-output>");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: 运行测试验证通过**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~ToolOutputLimiterDecoratorTests"
```

Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Decorators/ToolOutputLimiterDecorator.cs tests/Seeing.Agent.Tests/Decorators/ToolOutputLimiterDecoratorTests.cs
git commit -m "feat(output-limiter): ToolOutputLimiterDecorator 超限落盘 + 头尾预览 + 能力感知"
```

---

### Task 6: DI 注册装饰器链与输出存储

**Files:**
- Modify: `src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs`

- [ ] **Step 1: 注册 IToolOutputStore**

在 `ServiceCollectionExtensions.cs` 的会话管理器注册块（`:414` `AddSingleton<ISessionManager>` 之后）追加：

```csharp
            // 工具输出落盘服务（超限工具输出写会话 ref 目录）
            services.AddSingleton<Seeing.Agent.Output.IToolOutputStore>(sp =>
                new Seeing.Agent.Output.SessionToolOutputStore(
                    sp.GetRequiredService<ISessionStore>(),
                    sp.GetService<ILogger<Seeing.Agent.Output.SessionToolOutputStore>>()));
```

- [ ] **Step 2: 装饰器链最后注册 Limiter**

在装饰器链注册块（`:520-528` 的 Cache 装饰器 if 块**之后**、`return registry;` 之前）追加：

```csharp
                // 最外层兜底：输出限制装饰器（后注册=最外层，处理最终返回给调用方的结果）
                registry.Register(tool => new ToolOutputLimiterDecorator(
                    tool,
                    sp.GetRequiredService<IOptionsMonitor<SeeingAgentOptions>>(),
                    sp.GetRequiredService<Seeing.Agent.Output.IToolOutputStore>(),
                    sp.GetRequiredService<Core.Permission.IWorkspaceWhitelist>(),
                    loggerFactory.CreateLogger<ToolOutputLimiterDecorator>()));
```

确认该文件顶部已 `using Seeing.Agent.Decorators;`（`RetryToolDecorator`/`ToolTimeoutDecorator` 已在用，存在）。若 `IWorkspaceWhitelist` 需完整命名空间，使用 `Core.Permission.IWorkspaceWhitelist`（文件内已有 `using Seeing.Agent.Core.Permission;` 则直接用 `IWorkspaceWhitelist`）。

- [ ] **Step 3: 构建验证**

```bash
dotnet build src/Seeing.Agent
```

Expected: 构建成功，无警告错误。

- [ ] **Step 4: 全量装饰器测试回归**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~Decorator"
```

Expected: 现有装饰器测试全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Seeing.Agent/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(output-limiter): DI 注册输出存储与装饰器链最外层兜底"
```

---

### Task 7: ReadTool 声明 output.skip 豁免

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/FileSystem/ReadTool.cs`

- [ ] **Step 1: 添加能力属性**

在 `ReadTool.cs` 的类声明 `public class ReadTool : BuiltInToolBase` 上方加属性：

```csharp
    /// <summary>
    /// 读取工具 - 自身已实现 2000 行/50KB 限制与 offset/limit 分页，
    /// 声明 output.skip=true 豁免全局输出限制，避免行号前缀导致的边界冗余落盘。
    /// </summary>
    [ToolCapability(ToolCapabilityKeys.OutputSkip, "true")]
    public class ReadTool : BuiltInToolBase
```

确认文件顶部已有 `using Seeing.Agent.Abstractions.Tools;`（存在，见 `ReadTool.cs:1`）。

- [ ] **Step 2: 构建验证**

```bash
dotnet build src/Seeing.Agent
```

Expected: 构建成功。

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/FileSystem/ReadTool.cs
git commit -m "feat(output-limiter): ReadTool 声明 output.skip 豁免（自身分页+截断）"
```

---

### Task 8: BashTool 清理冗余 Metadata 预览

**Files:**
- Modify: `src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs`

- [ ] **Step 1: 移除冗余 output 预览**

`src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs` 中 `ExecuteCommandAsync` 末尾（当前 `:302-316`）：

删除 `truncatedOutput` 变量的计算：

```csharp
            // 截断输出以避免过长的数据
            var truncatedOutput = output.Length > MaxMetadataLength
                ? output.Substring(0, MaxMetadataLength) + "\n\n..."
                : output;
```

并将 `Success` 的 metadata 中 `["output"] = truncatedOutput,` 一行移除（保留 `["exit"]`/`["description"]`/`["timedOut"]`/`["aborted"]`），同时 `_logger.LogInformation` 前无需变化。修改后为：

```csharp
            _logger.LogInformation("命令执行完成，退出码: {ExitCode}", process.ExitCode);

            return Success(description, output, new Dictionary<string, object>
            {
                ["exit"] = process.ExitCode,
                ["description"] = description,
                ["timedOut"] = timedOut,
                ["aborted"] = aborted
            });
```

`MaxMetadataLength` 常量仍被 `UpdateMetadata`（`bash_output` MetadataSink 预览，供 UI）使用，**保留**。

- [ ] **Step 2: 回归验证 BashTool 测试**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll --TestCaseFilter:"FullyQualifiedName~BashTool"
```

Expected: 现有 BashTool 测试全部 PASS（无测试引用 `metadata["output"]`）。

- [ ] **Step 3: Commit**

```bash
git add src/Seeing.Agent/Tools/BuiltIn/Shell/BashTool.cs
git commit -m "refactor(output-limiter): BashTool 移除冗余 metadata 预览，完整输出交由装饰器统一限制"
```

---

### Task 9: 全量验证与收尾

**Files:**
- Modify: 无（验证）

- [ ] **Step 1: 全量构建**

```bash
dotnet build Seeing.Agent.slnx
```

Expected: 全部项目构建成功。

- [ ] **Step 2: 全量测试**

```bash
dotnet build tests/Seeing.Agent.Tests
dotnet vstest tests/Seeing.Agent.Tests/bin/Debug/net10.0/Seeing.Agent.Tests.dll
dotnet build tests/Seeing.Session.Tests
dotnet vstest tests/Seeing.Session.Tests/bin/Debug/net10.0/Seeing.Session.Tests.dll
```

Expected: 全部测试 PASS，无回归。

- [ ] **Step 3: 手工冒烟（可选）**

在 WebUI/Cli 运行一次会触发超长输出的工具（如 `bash` 执行 `Get-Content` 大文件），确认：
1. 事件流/UI 显示 `<persisted-output>` 预览（头+尾）；
2. `{会话存储目录}/{sessionId}.ref/{callId}.txt` 存在且为完整输出；
3. agent 后续可用 `read` 读取该文件（无权限拦截）。

- [ ] **Step 4: 更新 AGENTS.md 已知问题表**

`AGENTS.md`「已知问题」表新增一行（状态：修复中），记录 BashTool 超长输出直发 LLM 的问题已由统一输出限制解决：

```markdown
| **P1** | BashTool 完整输出直发 LLM + 无统一输出限制 | 已完成（ToolOutputLimiterDecorator 装饰器层兜底） |
```

- [ ] **Step 5: Commit**

```bash
git add AGENTS.md
git commit -m "docs: 记录工具输出统一限制已完成"
```

---

## Self-Review 结论

- **Spec 覆盖**：能力键（Task 1）、配置（Task 1）、BaseDirectory（Task 2）、ref 清理（Task 3）、落盘服务（Task 4）、装饰器（Task 5）、DI 注册（Task 6）、ReadTool 豁免（Task 7）、BashTool 修复（Task 8）、验证（Task 9）——spec 全部要求均有对应任务。
- **占位符**：无 TBD/TODO；每步含完整代码与命令。
- **类型一致性**：`ToolOutputOptions.MaxInlineBytes`/`PreviewHeadChars`/`PreviewTailChars`、`ToolCapabilityKeys.OutputSkip`/`OutputMaxBytes`、`IToolOutputStore.SaveAsync(sessionId, callId, content, ct)`、`IRelocatableSessionStore.BaseDirectory` 在任务间一致。
