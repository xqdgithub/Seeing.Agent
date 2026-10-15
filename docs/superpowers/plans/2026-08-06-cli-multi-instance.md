# CLI 多实例运行管理实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `seeing-cli` 支持多项目实例并存、可从任意文件夹停止、并在停止时展示并选择实例的启动文件夹。

**Architecture:** 引入全局运行注册表 `~/.seeing/instances.json`，作为所有运行实例（webui/gateway）的唯一事实来源。`start` 分配端口并注册，`stop`/`status` 从注册表读取实例（含启动文件夹、端口、PID），支持多实例交互选择。每次命令入口剪除 PID 已失效的僵尸条目。

**Tech Stack:** .NET 10、C#、System.CommandLine、xUnit v3 + FluentAssertions。

---

## 文件结构

**新增（CLI 服务层）：**
- Create: `samples/Seeing.Agent.Cli/Services/InstanceRecord.cs` — 实例数据模型
- Create: `samples/Seeing.Agent.Cli/Services/InstanceRegistry.cs` — 注册表读写 + 僵尸剪除
- Create: `samples/Seeing.Agent.Cli/Services/PortAllocator.cs` — 端口分配

**修改：**
- Modify: `samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs` — 集成注册表，Start 返回实例、Stop 按实例
- Modify: `samples/Seeing.Agent.Cli/Services/ManagementApiClient.cs` — 已有 `ReachableAsync`（webui 就绪）保留
- Modify: `samples/Seeing.Agent.Cli/Commands/StartCommand.cs` — 端口分配 + 注册 + 按服务就绪检测
- Modify: `samples/Seeing.Agent.Cli/Commands/StopCommand.cs` — 展示实例 + 多实例选择 + 关闭
- Modify: `samples/Seeing.Agent.Cli/Commands/StatusCommand.cs` — 列出全部实例及文件夹

**测试项目（新增）：**
- Create: `tests/Seeing.Agent.Cli.Tests/Seeing.Agent.Cli.Tests.csproj`
- Create: `tests/Seeing.Agent.Cli.Tests/InstanceRegistryTests.cs`
- Create: `tests/Seeing.Agent.Cli.Tests/PortAllocatorTests.cs`
- Create: `tests/Seeing.Agent.Cli.Tests/ManagementApiClientTests.cs`

**其他：**
- Modify: `samples/Seeing.Agent.Cli/Properties/AssemblyInfo.cs`（新增）— `InternalsVisibleTo`
- Modify: `Seeing.Agent.slnx` — 加入测试项目

---

### Task 1: 新增测试项目并接入解决方案

**Files:**
- Create: `tests/Seeing.Agent.Cli.Tests/Seeing.Agent.Cli.Tests.csproj`
- Create: `samples/Seeing.Agent.Cli/Properties/AssemblyInfo.cs`
- Modify: `Seeing.Agent.slnx`

- [ ] **Step 1: 创建测试项目文件**

创建 `tests/Seeing.Agent.Cli.Tests/Seeing.Agent.Cli.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Cli.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\samples\Seeing.Agent.Cli\Seeing.Agent.Cli.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 暴露内部类型给测试程序集**

创建 `samples/Seeing.Agent.Cli/Properties/AssemblyInfo.cs`：

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Seeing.Agent.Cli.Tests")]
```

- [ ] **Step 3: 将测试项目加入解决方案**

在 `Seeing.Agent.slnx` 的 `<Project Path="tests/Seeing.Gateway.WeCom.Tests/..."/>` 之后追加一行：

```xml
    <Project Path="tests/Seeing.Agent.Cli.Tests/Seeing.Agent.Cli.Tests.csproj" />
```

(如果 `.slnx` 有分组节，放入 tests 分组。)

- [ ] **Step 4: 验证可引用编译**

运行：
```bash
dotnet build tests/Seeing.Agent.Cli.Tests/Seeing.Agent.Cli.Tests.csproj
```
期望：构建成功（此时尚无测试源文件，仅空测试项目引用 CLI 项目，能通过 `InternalsVisibleTo` 访问内部类型）。

- [ ] **Step 5: 提交**
```bash
git add Seeing.Agent.slnx tests/Seeing.Agent.Cli.Tests samples/Seeing.Agent.Cli/Properties/AssemblyInfo.cs
git commit -m "test(cli): 新增 CLI 测试项目并暴露内部类型"
```

---

### Task 2: 实例模型 InstanceRecord

**Files:**
- Create: `samples/Seeing.Agent.Cli/Services/InstanceRecord.cs`

- [ ] **Step 1: 创建数据模型**

创建 `samples/Seeing.Agent.Cli/Services/InstanceRecord.cs`：

```csharp
namespace Seeing.Agent.Cli.Services;

public sealed class InstanceRecord
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string WorkspaceRoot { get; set; } = string.Empty;
    public int Port { get; set; }
    public DateTime StartedAt { get; set; }
}
```

- [ ] **Step 2: 构建 CLI 项目确认编译通过**

运行：
```bash
dotnet build samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj
```
期望：构建成功。

- [ ] **Step 3: 提交**
```bash
git add samples/Seeing.Agent.Cli/Services/InstanceRecord.cs
git commit -m "feat(cli): 新增实例模型 InstanceRecord"
```

---

### Task 3: 注册表 InstanceRegistry（TDD）

**Files:**
- Create: `samples/Seeing.Agent.Cli/Services/InstanceRegistry.cs`
- Test: `tests/Seeing.Agent.Cli.Tests/InstanceRegistryTests.cs`

- [ ] **Step 1: 编写失败测试**

创建 `tests/Seeing.Agent.Cli.Tests/InstanceRegistryTests.cs`：

```csharp
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class InstanceRegistryTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"seeing-cli-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Add_Then_Load_ShouldReturnRecord()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);

        registry.Add(new InstanceRecord
        {
            Id = "webui-abc123",
            Service = "webui",
            Pid = 1234,
            WorkspaceRoot = @"D:\projA",
            Port = 5000,
            StartedAt = DateTime.UtcNow
        });

        var loaded = registry.Load();
        loaded.Should().ContainSingle();
        loaded[0].Id.Should().Be("webui-abc123");
        loaded[0].WorkspaceRoot.Should().Be(@"D:\projA");
        loaded[0].Port.Should().Be(5000);
    }

    [Fact]
    public void Remove_ShouldDeleteMatchingRecord()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);
        registry.Add(new InstanceRecord { Id = "a", Service = "webui", Pid = 1 });
        registry.Add(new InstanceRecord { Id = "b", Service = "gateway", Pid = 2 });

        registry.Remove("a");

        registry.Load().Should().ContainSingle(r => r.Id == "b");
    }

    [Fact]
    public void PruneDead_ShouldRemoveRecordsWithDeadPid()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);
        registry.Add(new InstanceRecord { Id = "dead", Service = "webui", Pid = 999999 });
        registry.Add(new InstanceRecord { Id = "alive", Service = "webui", Pid = Environment.ProcessId });

        var alive = registry.PruneDead();

        alive.Should().ContainSingle(r => r.Id == "alive");
        registry.Load().Should().ContainSingle(r => r.Id == "alive");
    }

    [Fact]
    public void Load_WhenFileMissing_ShouldReturnEmpty()
    {
        using var dir = new TempDir(TempPath());
        var registry = new InstanceRegistry(dir.Path);
        registry.Load().Should().BeEmpty();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string path) { Path = path; Directory.CreateDirectory(path); }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

运行：
```bash
dotnet test tests/Seeing.Agent.Cli.Tests --filter "FullyQualifiedName~InstanceRegistryTests"
```
期望：编译失败（`InstanceRegistry` 不存在）。

- [ ] **Step 3: 实现 InstanceRegistry**

创建 `samples/Seeing.Agent.Cli/Services/InstanceRegistry.cs`：

```csharp
using System.Text.Json;

namespace Seeing.Agent.Cli.Services;

public sealed class InstanceRegistry
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public InstanceRegistry(string? directory = null)
    {
        var dir = directory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "instances.json");
    }

    public string FilePath => _filePath;

    public List<InstanceRecord> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath)) return new List<InstanceRecord>();
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<RegistryDocument>(json)?.Instances
                    ?? new List<InstanceRecord>();
            }
            catch
            {
                return new List<InstanceRecord>();
            }
        }
    }

    public void Add(InstanceRecord record)
    {
        lock (_lock)
        {
            var list = Load();
            list.Add(record);
            SaveCore(list);
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(i => i.Id == id);
            SaveCore(list);
        }
    }

    public List<InstanceRecord> PruneDead()
    {
        lock (_lock)
        {
            var list = Load();
            var alive = list.Where(i => IsProcessAlive(i.Pid)).ToList();
            if (alive.Count != list.Count) SaveCore(alive);
            return alive;
        }
    }

    private void SaveCore(List<InstanceRecord> list)
    {
        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(
                new RegistryDocument { Instances = list },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private sealed class RegistryDocument
    {
        public List<InstanceRecord> Instances { get; set; } = new();
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

运行：
```bash
dotnet test tests/Seeing.Agent.Cli.Tests --filter "FullyQualifiedName~InstanceRegistryTests"
```
期望：全部通过。

- [ ] **Step 5: 提交**
```bash
git add tests/Seeing.Agent.Cli.Tests/InstanceRegistryTests.cs samples/Seeing.Agent.Cli/Services/InstanceRegistry.cs
git commit -m "feat(cli): 实现实例注册表 InstanceRegistry（含 TDD）"
```

---

### Task 4: 端口分配器 PortAllocator（TDD）

**Files:**
- Create: `samples/Seeing.Agent.Cli/Services/PortAllocator.cs`
- Test: `tests/Seeing.Agent.Cli.Tests/PortAllocatorTests.cs`

- [ ] **Step 1: 编写失败测试**

创建 `tests/Seeing.Agent.Cli.Tests/PortAllocatorTests.cs`：

```csharp
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class PortAllocatorTests
{
    [Fact]
    public void NextAvailable_WhenPreferredFree_ShouldReturnPreferred()
    {
        var allocator = new PortAllocator();
        var port = allocator.NextAvailable(25000);
        port.Should().Be(25000);
    }

    [Fact]
    public void NextAvailable_WhenPreferredInUse_ShouldReturnLaterFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 25100);
        listener.Start();
        try
        {
            var allocator = new PortAllocator();
            var port = allocator.NextAvailable(25100);
            port.Should().BeGreaterThan(25100);
        }
        finally
        {
            listener.Stop();
        }
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

运行：
```bash
dotnet test tests/Seeing.Agent.Cli.Tests --filter "FullyQualifiedName~PortAllocatorTests"
```
期望：编译失败（`PortAllocator` 不存在）。

- [ ] **Step 3: 实现 PortAllocator**

创建 `samples/Seeing.Agent.Cli/Services/PortAllocator.cs`：

```csharp
using System.Net;
using System.Net.Sockets;

namespace Seeing.Agent.Cli.Services;

public sealed class PortAllocator
{
    public int NextAvailable(int preferred, int maxAttempts = 100)
    {
        for (var port = preferred; port < preferred + maxAttempts; port++)
        {
            if (!IsInUse(port)) return port;
        }

        throw new InvalidOperationException(
            $"在 {preferred}~{preferred + maxAttempts - 1} 范围内找不到可用端口");
    }

    private static bool IsInUse(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

运行：
```bash
dotnet test tests/Seeing.Agent.Cli.Tests --filter "FullyQualifiedName~PortAllocatorTests"
```
期望：全部通过。

- [ ] **Step 5: 提交**
```bash
git add tests/Seeing.Agent.Cli.Tests/PortAllocatorTests.cs samples/Seeing.Agent.Cli/Services/PortAllocator.cs
git commit -m "feat(cli): 实现端口分配器 PortAllocator（含 TDD）"
```

---

### Task 5: 就绪检测修复（TDD）

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Services/ManagementApiClient.cs`（保留已加的 `ReachableAsync`）
- Test: `tests/Seeing.Agent.Cli.Tests/ManagementApiClientTests.cs`

说明：`ManagementApiClient` 已有 `HealthCheckAsync`（GET `/api/gateway/health`，仅成功码为就绪）和 `ReachableAsync`（GET `/`，任意 HTTP 响应即就绪）。webui 用 `ReachableAsync`，gateway 用 `HealthCheckAsync`。

- [ ] **Step 1: 编写失败测试**

创建 `tests/Seeing.Agent.Cli.Tests/ManagementApiClientTests.cs`：

```csharp
using System.Net;
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class ManagementApiClientTests
{
    [Fact]
    public async Task ReachableAsync_WhenServerResponds_ShouldReturnTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        using var client = new ManagementApiClient("http://127.0.0.1:5000", handler);

        var result = await client.ReachableAsync();

        result.Should().BeTrue();
        handler.LastPath.Should().Be("/");
    }

    [Fact]
    public async Task ReachableAsync_WhenNoResponse_ShouldReturnFalse()
    {
        var handler = new StubHandler(null, "");
        using var client = new ManagementApiClient("http://127.0.0.1:5000", handler);

        (await client.ReachableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenNotFound_ShouldReturnFalse()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "");
        using var client = new ManagementApiClient("http://127.0.0.1:8765", handler);

        (await client.HealthCheckAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HealthCheckAsync_WhenSuccess_ShouldReturnTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        using var client = new ManagementApiClient("http://127.0.0.1:8765", handler);

        (await client.HealthCheckAsync()).Should().BeTrue();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode? _status;
        private readonly string _body;
        public string? LastPath { get; private set; }

        public StubHandler(HttpStatusCode? status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            if (_status == null)
                throw new HttpRequestException("连接失败");
            var response = new HttpResponseMessage(_status.Value)
            {
                Content = new StringContent(_body)
            };
            return Task.FromResult(response);
        }
    }
}
```

- [ ] **Step 2: 确认 `ReachableAsync` 已存在（否则先补齐）**

读取 `samples/Seeing.Agent.Cli/Services/ManagementApiClient.cs`，确认包含：

```csharp
public async Task<bool> ReachableAsync(CancellationToken ct = default)
{
    try
    {
        var response = await _http.GetAsync(_baseUrl, ct);
        return true;
    }
    catch
    {
        return false;
    }
}
```
若缺失则补上（放在 `HealthCheckAsync` 之后）。

- [ ] **Step 3: 运行测试确认通过**

运行：
```bash
dotnet test tests/Seeing.Agent.Cli.Tests --filter "FullyQualifiedName~ManagementApiClientTests"
```
期望：全部通过（这是对既有逻辑的回归锁定，验证 webui/gateway 就绪分支正确）。

- [ ] **Step 4: 提交**
```bash
git add tests/Seeing.Agent.Cli.Tests/ManagementApiClientTests.cs samples/Seeing.Agent.Cli/Services/ManagementApiClient.cs
git commit -m "test(cli): 锁定就绪检测行为（webui 可达性 vs gateway health）"
```

---

### Task 6: 重构 ServiceProcessManager 集成注册表

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs`

- [ ] **Step 1: 重写 ServiceProcessManager**

将 `samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs` 全文替换为：

```csharp
using System.Diagnostics;

namespace Seeing.Agent.Cli.Services;

public sealed class ServiceProcessManager
{
    private readonly string _workspaceRoot;
    public InstanceRegistry Registry { get; }

    public ServiceProcessManager(string workspaceRoot, string? registryDirectory = null)
    {
        _workspaceRoot = workspaceRoot;
        Registry = new InstanceRegistry(registryDirectory);
    }

    public async Task<InstanceRecord> StartAsync(
        string service,
        string dllPath,
        int port,
        string[]? extraArgs = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"找不到 {service} 的 dll 文件: {dllPath}");

        var args = $"\"{dllPath}\"";
        if (extraArgs is { Length: > 0 })
            args += " " + string.Join(" ", extraArgs);

        var psi = new ProcessStartInfo("dotnet", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workspaceRoot
        };

        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"无法启动 {service} 进程");

        var record = new InstanceRecord
        {
            Id = $"{service}-{Guid.NewGuid().ToString("N")[..6]}",
            Service = service,
            Pid = process.Id,
            WorkspaceRoot = _workspaceRoot,
            Port = port,
            StartedAt = DateTime.UtcNow
        };

        Registry.Add(record);
        return record;
    }

    public async Task<bool> StopAsync(
        InstanceRecord record,
        ManagementApiClient apiClient,
        CancellationToken ct = default)
    {
        var shutdownOk = await apiClient.ShutdownAsync(ct);

        try
        {
            var process = Process.GetProcessById(record.Pid);
            var exited = process.WaitForExit(10_000);
            if (!exited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已退出
        }

        Registry.Remove(record.Id);
        return shutdownOk;
    }

    public async Task WaitForReadyAsync(
        ManagementApiClient apiClient,
        bool checkGatewayHealth,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var ready = checkGatewayHealth
                ? await apiClient.HealthCheckAsync(ct)
                : await apiClient.ReachableAsync(ct);
            if (ready)
                return;

            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"服务在 {timeoutSeconds} 秒内未就绪");
    }
}
```

- [ ] **Step 2: 构建 CLI 项目确认编译通过**

运行：
```bash
dotnet build samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj
```
期望：构建成功（引用 StartCommand 等调用的旧 API 需在 Task 7-9 中同步更新；若此处因旧调用报错，属预期，继续推进下述任务后统一编译）。

- [ ] **Step 3: 提交**
```bash
git add samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs
git commit -m "refactor(cli): ServiceProcessManager 集成实例注册表"
```

---

### Task 7: 重写 StartCommand

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Commands/StartCommand.cs`

- [ ] **Step 1: 重写 StartCommand**

将 `samples/Seeing.Agent.Cli/Commands/StartCommand.cs` 的 action 主体替换（保留 `ResolveWorkspaceRoot`、`FindDll`、`GetGatewayPort` 私有方法）：

```csharp
command.SetAction(async parseResult =>
{
    var service = parseResult.GetValue<string>(serviceArg);
    service = service.ToLowerInvariant();
    if (service != "webui" && service != "gateway")
    {
        Console.Error.WriteLine("错误: service 必须是 'webui' 或 'gateway'");
        Environment.ExitCode = 1;
        return;
    }

    try
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var manager = new ServiceProcessManager(workspaceRoot);
        manager.Registry.PruneDead();

        var running = manager.Registry.Load()
            .Where(i => i.Service == service && i.WorkspaceRoot == workspaceRoot)
            .ToList();
        if (running.Count > 0)
        {
            Console.WriteLine($"服务 {service} 已在 {workspaceRoot} 运行中（端口: {running[0].Port}）");
            return;
        }

        var cliDir = AppDomain.CurrentDomain.BaseDirectory;
        string dllName = service == "webui"
            ? "Seeing.Agent.WebUI.dll"
            : "Seeing.Gateway.Server.dll";
        var dllPath = FindDll(cliDir, dllName);

        // webui 动态分配端口；gateway 使用工作区配置端口（其自身据此监听）
        int port;
        string[]? extraArgs;
        if (service == "webui")
        {
            port = new PortAllocator().NextAvailable(5000);
            extraArgs = new[] { "--urls", $"http://0.0.0.0:{port}" };
        }
        else
        {
            port = GetGatewayPort(workspaceRoot);
            extraArgs = Array.Empty<string>();
        }

        Console.WriteLine($"正在启动 {service}...");
        var record = await manager.StartAsync(service, dllPath, port, extraArgs);

        using var apiClient = new ManagementApiClient($"http://127.0.0.1:{port}");
        Console.Write($"等待 {service} 就绪");
        try
        {
            await manager.WaitForReadyAsync(apiClient, checkGatewayHealth: service == "gateway");
            Console.WriteLine(" 就绪!");
        }
        catch
        {
            await manager.StopAsync(record, apiClient, CancellationToken.None);
            throw;
        }

        if (service == "webui")
            Console.WriteLine($"WebUI 已启动: http://0.0.0.0:{port}（工作区: {workspaceRoot}）");
        else
            Console.WriteLine($"Gateway 已启动: http://127.0.0.1:{port}（工作区: {workspaceRoot}）");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"启动失败: {ex.Message}");
        Environment.ExitCode = 1;
    }
});
```

- [ ] **Step 2: 构建 CLI 项目确认编译通过**

运行：
```bash
dotnet build samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj
```
期望：构建成功。

- [ ] **Step 3: 提交**
```bash
git add samples/Seeing.Agent.Cli/Commands/StartCommand.cs
git commit -m "feat(cli): start 支持端口分配与实例注册"
```

---

### Task 8: 重写 StopCommand（多实例交互选择）

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Commands/StopCommand.cs`

- [ ] **Step 1: 重写 StopCommand**

将 `samples/Seeing.Agent.Cli/Commands/StopCommand.cs` 的 action 主体替换（保留 `ResolveWorkspaceRoot`）：

```csharp
command.SetAction(async parseResult =>
{
    var service = parseResult.GetValue<string>(serviceArg);
    service = service.ToLowerInvariant();
    if (service != "webui" && service != "gateway")
    {
        Console.Error.WriteLine("错误: service 必须是 'webui' 或 'gateway'");
        Environment.ExitCode = 1;
        return;
    }

    try
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var manager = new ServiceProcessManager(workspaceRoot);
        manager.Registry.PruneDead();

        var candidates = manager.Registry.Load()
            .Where(i => i.Service == service)
            .ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine($"服务 {service} 未在运行");
            return;
        }

        InstanceRecord target;
        if (candidates.Count == 1)
        {
            target = candidates[0];
        }
        else
        {
            Console.WriteLine($"找到 {candidates.Count} 个 {service} 实例，选择要关闭的编号:");
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                Console.WriteLine(
                    $"  [{i + 1}] 文件夹: {c.WorkspaceRoot}  端口: {c.Port}  PID: {c.Pid}  启动: {c.StartedAt:yyyy-MM-dd HH:mm}");
            }
            Console.Write("编号: ");
            var input = Console.ReadLine();
            if (!int.TryParse(input, out var idx) || idx < 1 || idx > candidates.Count)
            {
                Console.Error.WriteLine("无效选择");
                Environment.ExitCode = 1;
                return;
            }
            target = candidates[idx - 1];
        }

        using var apiClient = new ManagementApiClient($"http://127.0.0.1:{target.Port}");
        Console.WriteLine($"正在停止 {service}（文件夹: {target.WorkspaceRoot}）...");
        var ok = await manager.StopAsync(target, apiClient, CancellationToken.None);

        Console.WriteLine(ok ? $"{service} 已停止" : $"{service} 已强制终止");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"停止失败: {ex.Message}");
        Environment.ExitCode = 1;
    }
});
```

- [ ] **Step 2: 构建 CLI 项目确认编译通过**

运行：
```bash
dotnet build samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj
```
期望：构建成功。

- [ ] **Step 3: 提交**
```bash
git add samples/Seeing.Agent.Cli/Commands/StopCommand.cs
git commit -m "feat(cli): stop 支持多实例展示与交互选择"
```

---

### Task 9: 重写 StatusCommand（列出实例及文件夹）

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Commands/StatusCommand.cs`

- [ ] **Step 1: 重写 StatusCommand**

将 `samples/Seeing.Agent.Cli/Commands/StatusCommand.cs` 的 action 主体替换（保留 `ResolveWorkspaceRoot`，删除不再使用的 `GetGatewayPort`）：

```csharp
command.SetAction(async parseResult =>
{
    try
    {
        var workspaceRoot = ResolveWorkspaceRoot();
        var manager = new ServiceProcessManager(workspaceRoot);
        manager.Registry.PruneDead();

        var instances = manager.Registry.Load()
            .OrderBy(i => i.Service)
            .ThenBy(i => i.WorkspaceRoot)
            .ToList();

        Console.WriteLine("服务状态:");
        Console.WriteLine(new string('-', 70));
        if (instances.Count == 0)
        {
            Console.WriteLine("  无运行中的服务");
            return;
        }

        foreach (var inst in instances)
        {
            Console.WriteLine(
                $"  {inst.Service,-8} 端口: {inst.Port,-5} PID: {inst.Pid,-7} 启动: {inst.StartedAt:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"           文件夹: {inst.WorkspaceRoot}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"状态查询失败: {ex.Message}");
        Environment.ExitCode = 1;
    }
});
```

- [ ] **Step 2: 构建 CLI 项目确认编译通过**

运行：
```bash
dotnet build samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj
```
期望：构建成功。

- [ ] **Step 3: 提交**
```bash
git add samples/Seeing.Agent.Cli/Commands/StatusCommand.cs
git commit -m "feat(cli): status 列出全部实例及启动文件夹"
```

---

### Task 10: 全量验证

- [ ] **Step 1: 运行全部 CLI 测试**

运行：
```bash
dotnet test tests/Seeing.Agent.Cli.Tests
```
期望：全部通过。

- [ ] **Step 2: 构建整个解决方案**

运行：
```bash
dotnet build Seeing.Agent.slnx
```
期望：构建成功，无错误。

- [ ] **Step 3: 手动冒烟验证（可选）**

1. 从项目 A 目录 `seeing-cli start webui` → 应显示就绪、端口 5000。
2. 从项目 B 目录再 `seeing-cli start webui` → 自动分配端口 5001。
3. `seeing-cli status` → 列出两个 webui 实例及各自文件夹。
4. `seeing-cli stop webui` → 展示两个实例供选择，选择后正确关闭对应进程。
5. 从任意目录重复 stop → 仍能定位并关闭。

- [ ] **Step 4: 提交收尾（如有改动）**
```bash
git add -A
git commit -m "chore(cli): 多实例管理收尾"
```

---

## 自检

- **Spec 覆盖**：注册表（Task 3）、端口分配（Task 4）、就绪修复（Task 5）、进程管理重构（Task 6）、start 注册（Task 7）、stop 交互选择+文件夹（Task 8）、status 文件夹（Task 9）、迁移（废弃 pid 文件改为注册表，Task 6 移除旧 PidFilePath/CleanupDeadPidFile）。全部覆盖。
- **占位符扫描**：无 TBD/TODO；每个改动步骤含完整代码。
- **类型一致性**：`InstanceRecord`（Id/Service/Pid/WorkspaceRoot/Port/StartedAt）在各任务中一致；`InstanceRegistry.Add/Remove/Load/PruneDead` 签名一致；`ServiceProcessManager.StartAsync(service,dllPath,port,extraArgs,ct)` 与 `StopAsync(record,apiClient,ct)`、`WaitForReadyAsync(apiClient,checkGatewayHealth,...)` 在命令中调用一致。