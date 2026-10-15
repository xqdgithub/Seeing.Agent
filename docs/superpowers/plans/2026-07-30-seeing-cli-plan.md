# Seeing.Agent.Cli Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `Seeing.Agent.Cli` console project under `samples/` providing CLI management commands (start/stop/status/config/agent/job/session) that share WebUI's DI container but skip Blazor/AntDesign.

**Architecture:** A .NET 10.0 console app using `System.CommandLine` for parsing. "Config" commands directly use DI-backed services (UnifiedConfigManager, IAgentRegistry, ISessionManager, IScheduleManager). "Service" commands (start/stop/status) manage subprocesses and talk to Gateway's `/api/admin/*` HTTP endpoints. New admin endpoints are added to `GatewayHost`.

**Tech Stack:** System.CommandLine, System.Text.Json, System.Diagnostics.Process, Microsoft.Extensions.Http, Spectre.Console (already in Directory.Packages.props for table formatting).

---

### Task 1: Add System.CommandLine package and create project

**Files:**
- Modify: `Directory.Packages.props`
- Create: `samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj`
- Create: `samples/Seeing.Agent.Cli/appsettings.json`
- Modify: `Seeing.Agent.slnx`

- [ ] **Step 1: Add System.CommandLine to Directory.Packages.props**

After the `<PackageVersion Include="Spectre.Console.Cli" Version="0.49.1" />` line, add:

```xml
    <PackageVersion Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
```

- [ ] **Step 2: Create project file**

Create `samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Seeing.Agent.Cli</RootNamespace>
    <AssemblyName>seeing-cli</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.CommandLine" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.Http" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Seeing.Agent.App\Seeing.Agent.App.csproj" />
    <ProjectReference Include="..\..\src\Seeing.Agent.Acp\Seeing.Agent.Acp.csproj" />
    <ProjectReference Include="..\..\src\Seeing.Agent.Gateway\Seeing.Agent.Gateway.csproj" />
    <ProjectReference Include="..\..\src\Seeing.Agent.Scheduler\Seeing.Agent.Scheduler.csproj" />
    <ProjectReference Include="..\..\src\Seeing.Agent.Memory\Seeing.Agent.Memory.csproj" />
    <ProjectReference Include="..\..\src\Seeing.Session\Seeing.Session.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create minimal appsettings.json**

Create `samples/Seeing.Agent.Cli/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Seeing.Agent.Cli": "Information"
    }
  }
}
```

- [ ] **Step 4: Add project reference to solution**

In `Seeing.Agent.slnx`, add the CLI project under the samples folder. After the ChannelHost line:

```xml
    <Project Path="samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj" />
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds (Program.cs can be empty `return 0;` for now)

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props samples/Seeing.Agent.Cli/ Seeing.Agent.slnx
git commit -m "feat: add Seeing.Agent.Cli project skeleton with System.CommandLine"
```

---

### Task 2: Add admin API endpoints to GatewayHost

**Files:**
- Create: `src/Seeing.Agent.Gateway/Endpoints/AdminEndpoints.cs`
- Modify: `src/Seeing.Agent.Gateway/Hosting/GatewayHost.cs`

- [ ] **Step 1: Create AdminEndpoints class**

Create `src/Seeing.Agent.Gateway/Endpoints/AdminEndpoints.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Gateway.Core;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Session.Core;

namespace Seeing.Agent.Gateway.Endpoints;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static DateTime _startTime = DateTime.UtcNow;

    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/status", GetStatusAsync);
        app.MapPost("/api/admin/shutdown", ShutdownAsync);
        app.MapGet("/api/admin/sessions", GetSessionsAsync);
        app.MapGet("/api/admin/jobs", GetJobsAsync);
        return app;
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext httpContext,
        GatewayRunTracker runTracker,
        ISessionManager sessionManager,
        IScheduleManager? scheduleManager)
    {
        var uptime = DateTime.UtcNow - _startTime;
        var schedulerRunning = scheduleManager?.IsStarted ?? false;
        var sessions = sessionManager.List();

        return Results.Ok(new
        {
            status = "running",
            uptime = $"{(int)uptime.TotalHours}h {uptime.Minutes}m",
            uptimeSeconds = uptime.TotalSeconds,
            gatewayPort = httpContext.Connection.LocalPort,
            activeSessions = sessions.Count,
            activeExecutions = 0, // GatewayRunTracker tracks by executionId; count approximated via sessions
            schedulerRunning = schedulerRunning
        });
    }

    private static IResult ShutdownAsync(HttpContext httpContext)
    {
        var lifetime = httpContext.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();
        return Results.Ok(new { message = "shutting down" });
    }

    private static async Task<IResult> GetSessionsAsync(
        ISessionManager sessionManager,
        CancellationToken ct)
    {
        var sessions = sessionManager.List();
        var result = sessions.Select(s => new
        {
            id = s.Id,
            agent = s.SelectedAgent ?? "-",
            messageCount = s.Messages.Count,
            createdAt = s.CreatedAt,
            updatedAt = s.UpdatedAt
        });
        return Results.Ok(result);
    }

    private static async Task<IResult> GetJobsAsync(
        IScheduleManager? scheduleManager,
        CancellationToken ct)
    {
        if (scheduleManager == null)
            return Results.Ok(Array.Empty<object>());

        var statuses = await scheduleManager.GetAllJobStatusesAsync(ct);
        var result = statuses.Select(s => new
        {
            id = s.JobId,
            name = s.JobName ?? s.JobId,
            state = s.State.ToString(),
            previousFireTime = s.PreviousFireTime,
            nextFireTime = s.NextFireTime,
            lastError = s.LastError
        });
        return Results.Ok(result);
    }
}
```

- [ ] **Step 2: Wire up admin endpoints and register dependencies in GatewayHost**

In `src/Seeing.Agent.Gateway/Hosting/GatewayHost.cs`:

After the line `builder.Services.AddSingleton(sessionService);` (around line 90), add registrations for `ISessionManager` and `IScheduleManager` so admin endpoints can resolve them:

```csharp
        // Register root-level services for admin endpoints
        builder.Services.AddSingleton(_rootServices.GetRequiredService<ISessionManager>());
        builder.Services.AddSingleton(_rootServices.GetRequiredService<IScheduleManager>());
```

After `app.MapGatewayEndpoints();` (line 99), add:

```csharp
        app.MapAdminEndpoints();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/Seeing.Agent.Gateway`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Seeing.Agent.Gateway/Endpoints/AdminEndpoints.cs src/Seeing.Agent.Gateway/Hosting/GatewayHost.cs
git commit -m "feat: add /api/admin/* endpoints for CLI management"
```

---

### Task 3: Implement ManagementApiClient

**Files:**
- Create: `samples/Seeing.Agent.Cli/Services/ManagementApiClient.cs`

- [ ] **Step 1: Create ManagementApiClient**

Create `samples/Seeing.Agent.Cli/Services/ManagementApiClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace Seeing.Agent.Cli.Services;

public sealed class ManagementApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ManagementApiClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public ManagementApiClient(string baseUrl, HttpMessageHandler handler)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<AdminStatusResponse?> GetStatusAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{_baseUrl}/api/admin/status", ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<AdminStatusResponse>(cancellationToken: ct);
    }

    public async Task<bool> ShutdownAsync(CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"{_baseUrl}/api/admin/shutdown", null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/gateway/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class AdminStatusResponse
{
    public string Status { get; set; } = string.Empty;
    public string Uptime { get; set; } = string.Empty;
    public double UptimeSeconds { get; set; }
    public int GatewayPort { get; set; }
    public int ActiveSessions { get; set; }
    public int ActiveExecutions { get; set; }
    public bool SchedulerRunning { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Services/ManagementApiClient.cs
git commit -m "feat: add ManagementApiClient for Gateway admin HTTP calls"
```

---

### Task 4: Implement ServiceProcessManager

**Files:**
- Create: `samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs`

- [ ] **Step 1: Create ServiceProcessManager**

Create `samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs`:

```csharp
using System.Diagnostics;

namespace Seeing.Agent.Cli.Services;

public sealed class ServiceProcessManager
{
    private readonly string _workspaceRoot;

    public ServiceProcessManager(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
    }

    private string PidFilePath(string service)
        => Path.Combine(_workspaceRoot, ".seeing", $"{service}.pid");

    public bool IsRunning(string service)
    {
        var pidPath = PidFilePath(service);
        if (!File.Exists(pidPath)) return false;

        try
        {
            var pidText = File.ReadAllText(pidPath).Trim();
            if (!int.TryParse(pidText, out var pid)) return false;

            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public async Task<Process?> StartAsync(
        string service,
        string dllPath,
        string[]? extraArgs = null,
        CancellationToken ct = default)
    {
        if (IsRunning(service))
            throw new InvalidOperationException($"服务 {service} 已在运行中");

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

        // Write PID
        var seeingDir = Path.Combine(_workspaceRoot, ".seeing");
        Directory.CreateDirectory(seeingDir);
        await File.WriteAllTextAsync(PidFilePath(service), process.Id.ToString(), ct);

        return process;
    }

    public async Task<bool> StopAsync(string service, ManagementApiClient apiClient, CancellationToken ct)
    {
        // Try graceful shutdown via API first
        var shutdownOk = await apiClient.ShutdownAsync(ct);

        // Wait for process to exit
        var pidPath = PidFilePath(service);
        if (File.Exists(pidPath))
        {
            try
            {
                var pidText = await File.ReadAllTextAsync(pidPath, ct);
                if (int.TryParse(pidText.Trim(), out var pid))
                {
                    var process = Process.GetProcessById(pid);
                    var exited = process.WaitForExit(10_000); // 10s timeout
                    if (!exited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                // Process already gone
            }

            File.Delete(pidPath);
        }

        return shutdownOk;
    }

    public async Task WaitForReadyAsync(
        ManagementApiClient apiClient,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await apiClient.HealthCheckAsync(ct))
                return;

            await Task.Delay(500, ct);
        }

        throw new TimeoutException($"服务在 {timeoutSeconds} 秒内未就绪");
    }

    public void CleanupDeadPidFile(string service)
    {
        var pidPath = PidFilePath(service);
        if (!File.Exists(pidPath)) return;

        if (!IsRunning(service))
            File.Delete(pidPath);
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs
git commit -m "feat: add ServiceProcessManager for subprocess lifecycle"
```

---

### Task 5: Implement CliServiceBootstrap

**Files:**
- Create: `samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs`

- [ ] **Step 1: Create CliServiceBootstrap**

Create `samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Acp.Extensions;
using Seeing.Agent.App;
using Seeing.Agent.Extensions;
using Seeing.Agent.Gateway.Extensions;
using Seeing.Agent.Memory.Extensions;
using Seeing.Agent.Scheduler.Extensions;

namespace Seeing.Agent.Cli.Infrastructure;

public static class CliServiceBootstrap
{
    /// <summary>
    /// 构建 DI 容器，复用 WebUI 的核心注册链。
    /// 不使用 Blazor/AntDesign/Circuit/MessageRendering。
    /// </summary>
    public static async Task<IHost> BuildHostAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSeeingAgent(builder.Configuration);
        builder.Services.AddSeeingAcp();
        builder.Services.AddSeeingScheduler();
        builder.Services.AddSeeingGatewayServer(builder.Configuration);
        builder.Services.AddMemoryServices();
        builder.Services.AddChatOrchestrator();

        var host = builder.Build();

        await host.Services.InitializeSeeingAgentAsync();

        return host;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Infrastructure/CliServiceBootstrap.cs
git commit -m "feat: add CliServiceBootstrap sharing WebUI's core DI chain"
```

---

### Task 6: Implement Program.cs with root command

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Program.cs`

- [ ] **Step 1: Write Program.cs**

Replace `samples/Seeing.Agent.Cli/Program.cs` with:

```csharp
using System.CommandLine;

namespace Seeing.Agent.Cli;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Seeing.Agent CLI 管理工具");

        // Subcommands will be added in following tasks
        rootCommand.AddCommand(StartCommand.Create());
        rootCommand.AddCommand(StopCommand.Create());
        rootCommand.AddCommand(StatusCommand.Create());
        rootCommand.AddCommand(ConfigCommand.Create());
        rootCommand.AddCommand(AgentCommand.Create());
        rootCommand.AddCommand(JobCommand.Create());
        rootCommand.AddCommand(SessionCommand.Create());

        return await rootCommand.InvokeAsync(args);
    }
}
```

- [ ] **Step 2: Build will fail (commands not yet defined) — expected**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: BUILD FAILS — `StartCommand`, `StopCommand`, etc. do not exist yet.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Program.cs
git commit -m "feat: add Program.cs with root command skeleton"
```

---

### Task 7: Implement StartCommand

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/StartCommand.cs`

- [ ] **Step 1: Create StartCommand**

Create `samples/Seeing.Agent.Cli/Commands/StartCommand.cs`:

```csharp
using System.CommandLine;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StartCommand
{
    public static Command Create()
    {
        var serviceArg = new Argument<string>("service", "要启动的服务: webui 或 gateway");

        var command = new Command("start", "启动指定的服务")
        {
            serviceArg
        };

        command.SetHandler(async (service) =>
        {
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

                manager.CleanupDeadPidFile(service);

                if (manager.IsRunning(service))
                {
                    Console.WriteLine($"服务 {service} 已在运行中");
                    return;
                }

                var cliDir = AppDomain.CurrentDomain.BaseDirectory;
                string dllName = service == "webui"
                    ? "Seeing.Agent.WebUI.dll"
                    : "Seeing.Gateway.Server.dll";

                var dllPath = FindDll(cliDir, dllName);
                var extraArgs = service == "webui"
                    ? new[] { "--urls", "http://0.0.0.0:5000" }
                    : Array.Empty<string>();

                Console.WriteLine($"正在启动 {service}...");
                var process = await manager.StartAsync(service, dllPath, extraArgs);

                // 从 seeing.json 获取 Gateway 端口
                var gatewayPort = GetGatewayPort(workspaceRoot);
                var apiClient = new ManagementApiClient($"http://127.0.0.1:{gatewayPort}");

                Console.Write($"等待 {service} 就绪");
                await manager.WaitForReadyAsync(apiClient);
                Console.WriteLine(" 就绪!");

                if (service == "webui")
                    Console.WriteLine($"WebUI 已启动: http://0.0.0.0:5000");
                else
                    Console.WriteLine($"Gateway 已启动: http://127.0.0.1:{gatewayPort}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"启动失败: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, serviceArg);

        return command;
    }

    private static string ResolveWorkspaceRoot()
    {
        var env = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return env;

        return Directory.GetCurrentDirectory();
    }

    private static string FindDll(string cliDir, string dllName)
    {
        // 1. Same directory as CLI
        var sameDir = Path.Combine(cliDir, dllName);
        if (File.Exists(sameDir)) return sameDir;

        // 2. Parent directory (when running from bin/Debug/net10.0/)
        var parentDir = Path.Combine(cliDir, "..", dllName);
        if (File.Exists(parentDir)) return Path.GetFullPath(parentDir);

        // 3. Parent of samples (repo root layout: src/Seeing.Agent.WebUI/bin/...)
        var repoRoot = Path.GetFullPath(Path.Combine(cliDir, "..", "..", "..", ".."));
        var webuiBuild = Path.Combine(repoRoot, "samples",
            dllName.Replace(".dll", ""), "bin", "Debug", "net10.0", dllName);
        if (File.Exists(webuiBuild)) return webuiBuild;

        // 4. Gateway server path
        var gatewayBuild = Path.Combine(repoRoot, "samples",
            dllName.Replace(".Server", ".Server").Replace(".dll", ""),
            "bin", "Debug", "net10.0", dllName);
        if (File.Exists(gatewayBuild)) return gatewayBuild;

        throw new FileNotFoundException(
            $"找不到 {dllName}。请先执行 dotnet build，或设置 SEEING_WORKSPACE_ROOT 环境变量");
    }

    private static int GetGatewayPort(string workspaceRoot)
    {
        try
        {
            var seeingJson = Path.Combine(workspaceRoot, ".seeing", "seeing.json");
            if (File.Exists(seeingJson))
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seeingJson));
                if (json.RootElement.TryGetProperty("SeeingAgent", out var sa) &&
                    sa.TryGetProperty("Gateway", out var gw) &&
                    gw.TryGetProperty("Port", out var port))
                {
                    return port.GetInt32();
                }
            }
        }
        catch { }

        return 8765; // default
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds (6 commands still missing, but System.CommandLine handler reference is runtime-based).

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Commands/StartCommand.cs
git commit -m "feat: add start command for webui/gateway subprocess launch"
```

---

### Task 8: Implement StopCommand

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/StopCommand.cs`

- [ ] **Step 1: Create StopCommand**

Create `samples/Seeing.Agent.Cli/Commands/StopCommand.cs`:

```csharp
using System.CommandLine;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StopCommand
{
    public static Command Create()
    {
        var serviceArg = new Argument<string>("service", "要停止的服务: webui 或 gateway");

        var command = new Command("stop", "停止指定的服务")
        {
            serviceArg
        };

        command.SetHandler(async (service) =>
        {
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

                if (!manager.IsRunning(service))
                {
                    Console.WriteLine($"服务 {service} 未在运行");
                    manager.CleanupDeadPidFile(service);
                    return;
                }

                var gatewayPort = GetGatewayPort(workspaceRoot);
                using var apiClient = new ManagementApiClient($"http://127.0.0.1:{gatewayPort}");

                Console.WriteLine($"正在停止 {service}...");
                var ok = await manager.StopAsync(service, apiClient);

                if (ok)
                    Console.WriteLine($"{service} 已停止");
                else
                    Console.WriteLine($"{service} 已强制终止");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"停止失败: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, serviceArg);

        return command;
    }

    private static string ResolveWorkspaceRoot()
    {
        var env = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        return Directory.GetCurrentDirectory();
    }

    private static int GetGatewayPort(string workspaceRoot)
    {
        try
        {
            var seeingJson = Path.Combine(workspaceRoot, ".seeing", "seeing.json");
            if (File.Exists(seeingJson))
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seeingJson));
                if (json.RootElement.TryGetProperty("SeeingAgent", out var sa) &&
                    sa.TryGetProperty("Gateway", out var gw) &&
                    gw.TryGetProperty("Port", out var port))
                    return port.GetInt32();
            }
        }
        catch { }
        return 8765;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Commands/StopCommand.cs
git commit -m "feat: add stop command for graceful service shutdown"
```

---

### Task 9: Implement StatusCommand

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/StatusCommand.cs`

- [ ] **Step 1: Create StatusCommand**

Create `samples/Seeing.Agent.Cli/Commands/StatusCommand.cs`:

```csharp
using System.CommandLine;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "查看所有服务运行状态");

        command.SetHandler(async () =>
        {
            try
            {
                var workspaceRoot = ResolveWorkspaceRoot();
                var manager = new ServiceProcessManager(workspaceRoot);

                Console.WriteLine("服务状态:");
                Console.WriteLine(new string('-', 50));

                foreach (var service in new[] { "webui", "gateway" })
                {
                    var running = manager.IsRunning(service);
                    var icon = running ? "[运行中]" : "[已停止]";
                    Console.WriteLine($"  {service,-10} {icon}");
                }

                // Try API for detailed status
                var gatewayPort = GetGatewayPort(workspaceRoot);
                using var apiClient = new ManagementApiClient($"http://127.0.0.1:{gatewayPort}");

                var status = await apiClient.GetStatusAsync();
                if (status != null)
                {
                    Console.WriteLine();
                    Console.WriteLine("Gateway 详情:");
                    Console.WriteLine(new string('-', 50));
                    Console.WriteLine($"  端口:       {status.GatewayPort}");
                    Console.WriteLine($"  运行时间:   {status.Uptime}");
                    Console.WriteLine($"  活跃会话:   {status.ActiveSessions}");
                    Console.WriteLine($"  活跃执行:   {status.ActiveExecutions}");
                    Console.WriteLine($"  调度器:     {(status.SchedulerRunning ? "运行中" : "已停止")}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"状态查询失败: {ex.Message}");
                Environment.ExitCode = 1;
            }
        });

        return command;
    }

    private static string ResolveWorkspaceRoot()
    {
        var env = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
        return Directory.GetCurrentDirectory();
    }

    private static int GetGatewayPort(string workspaceRoot)
    {
        try
        {
            var seeingJson = Path.Combine(workspaceRoot, ".seeing", "seeing.json");
            if (File.Exists(seeingJson))
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(seeingJson));
                if (json.RootElement.TryGetProperty("SeeingAgent", out var sa) &&
                    sa.TryGetProperty("Gateway", out var gw) &&
                    gw.TryGetProperty("Port", out var port))
                    return port.GetInt32();
            }
        }
        catch { }
        return 8765;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Commands/StatusCommand.cs
git commit -m "feat: add status command for service health checks"
```

---

### Task 10: Implement ConfigCommand

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/ConfigCommand.cs`

- [ ] **Step 1: Create ConfigCommand**

Create `samples/Seeing.Agent.Cli/Commands/ConfigCommand.cs`:

```csharp
using System.CommandLine;
using System.Text.Json;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Cli.Commands;

public static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", "管理配置");

        var showSectionArg = new Argument<string?>("section", () => null, "要显示的配置段（不指定则显示全部）");
        var showCommand = new Command("show", "查看配置") { showSectionArg };
        showCommand.SetHandler(async (section) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var config = host.Services.GetRequiredService<UnifiedConfigManager>();
            await ShowConfig(config, section);
        }, showSectionArg);

        var setPathArg = new Argument<string>("path", "配置路径（如 Gateway.Port）");
        var setValueArg = new Argument<string>("value", "配置值");
        var setCommand = new Command("set", "设置配置值") { setPathArg, setValueArg };
        setCommand.SetHandler(async (path, value) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var config = host.Services.GetRequiredService<UnifiedConfigManager>();
            await SetConfig(config, path, value);
        }, setPathArg, setValueArg);

        var initCommand = new Command("init", "初始化工作区");
        initCommand.SetHandler(async () =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var config = host.Services.GetRequiredService<UnifiedConfigManager>();
            await InitWorkspace(config);
        });

        command.AddCommand(showCommand);
        command.AddCommand(setCommand);
        command.AddCommand(initCommand);

        return command;
    }

    private static Task ShowConfig(UnifiedConfigManager config, string? section)
    {
        if (section != null)
        {
            var info = config.GetSourceInfo(section);
            if (info == null || !info.HasProjectLevel && !info.HasUserLevel)
            {
                Console.WriteLine($"未找到配置段: {section}");
                return Task.CompletedTask;
            }

            Console.WriteLine($"[{section}]");
            Console.WriteLine($"  来源: {info.SourceDescription}");
            Console.WriteLine($"  范围: {info.Scope}");

            if (info.Scope != ConfigScope.ProjectOnly)
                Console.WriteLine($"  用户级: {info.UserPath ?? "(无)"}");
            Console.WriteLine($"  项目级: {info.ProjectPath}");
        }
        else
        {
            var sections = config.GetAllSections();
            foreach (var (name, meta) in sections.OrderBy(s => s.Value.DisplayOrder))
            {
                var info = config.GetSourceInfo(name);
                Console.WriteLine($"[{name}] ({info.SourceDescription})");
            }
        }

        return Task.CompletedTask;
    }

    private static async Task SetConfig(UnifiedConfigManager config, string path, string value)
    {
        // Parse path: "Gateway.Port" → section="Gateway", rest="Port"
        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            Console.Error.WriteLine("错误: 路径格式应为 Section.Key，例如 Gateway.Port");
            Environment.ExitCode = 1;
            return;
        }

        var section = path[..dotIndex];
        var key = path[(dotIndex + 1)..];

        var meta = config.GetSectionMeta(section);
        if (meta == null)
        {
            Console.Error.WriteLine($"错误: 未注册的配置段 '{section}'");
            Environment.ExitCode = 1;
            return;
        }

        // For simplicity, we save via raw JSON manipulation
        // Convert value to correct type based on existing json
        var level = meta.Scope == ConfigScope.ProjectOnly ? ConfigLevel.Project : ConfigLevel.Project;
        var raw = await config.GetRawJsonAsync(level, meta.FileName);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        // Build the new value
        var newValue = ParseValue(value);
        var updatedJson = SetNestedValue(raw, meta.FileName == "seeing.json" ? "SeeingAgent" : null, section, key, newValue);

        await config.SaveRawJsonAsync(level, meta.FileName, updatedJson);
        Console.WriteLine($"已设置 {path} = {value}");
    }

    private static string SetNestedValue(string rawJson, string? rootProperty, string section, string key, string newValue)
    {
        using var doc = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
        System.Text.Json.Nodes.JsonNode target;

        if (rootProperty != null)
        {
            var root = doc?.AsObject();
            if (root == null) return rawJson;
            if (!root.ContainsKey(rootProperty))
                root[rootProperty] = new System.Text.Json.Nodes.JsonObject();
            target = root[rootProperty]!;
        }
        else
        {
            target = doc!;
        }

        var sectionNode = target.AsObject();
        if (!sectionNode.ContainsKey(section))
            sectionNode[section] = new System.Text.Json.Nodes.JsonObject();

        var sectionObj = sectionNode[section]?.AsObject();
        if (sectionObj != null)
        {
            // Try to infer type: if the value looks like int, store as int
            if (int.TryParse(newValue, out var intVal))
                sectionObj[key] = intVal;
            else if (bool.TryParse(newValue, out var boolVal))
                sectionObj[key] = boolVal;
            else
                sectionObj[key] = newValue;
        }

        return doc?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? rawJson;
    }

    private static string ParseValue(string raw) => raw;

    private static async Task InitWorkspace(UnifiedConfigManager config)
    {
        var workspaceRoot = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT")
            ?? Directory.GetCurrentDirectory();

        var seeingDir = Path.Combine(workspaceRoot, ".seeing");
        var agentsDir = Path.Combine(seeingDir, "agents");
        var sessionsDir = Path.Combine(seeingDir, "sessions");

        Directory.CreateDirectory(agentsDir);
        Directory.CreateDirectory(sessionsDir);

        var seeingJson = Path.Combine(seeingDir, "seeing.json");
        if (!File.Exists(seeingJson))
        {
            var template = @"{
  ""SeeingAgent"": {
    ""Gateway"": {
      ""Enabled"": true,
      ""Port"": 8765,
      ""BindAddress"": ""127.0.0.1""
    }
  }
}";
            await File.WriteAllTextAsync(seeingJson, template);
        }

        Console.WriteLine($"工作区已初始化: {seeingDir}");
        Console.WriteLine($"  {seeingJson}");
        Console.WriteLine($"  {agentsDir}/");
        Console.WriteLine($"  {sessionsDir}/");
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Commands/ConfigCommand.cs
git commit -m "feat: add config show/set/init commands"
```

---

### Task 11: Implement AgentCommand

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/AgentCommand.cs`

- [ ] **Step 1: Create AgentCommand**

Create `samples/Seeing.Agent.Cli/Commands/AgentCommand.cs`:

```csharp
using System.CommandLine;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Cli.Commands;

public static class AgentCommand
{
    public static Command Create()
    {
        var command = new Command("agent", "管理代理");

        var listCommand = new Command("list", "列出所有代理");
        listCommand.SetHandler(async () =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var registry = host.Services.GetRequiredService<IAgentRegistry>();
            var agents = await registry.GetAgentsAsync();

            Console.WriteLine($"{"Name",-20} {"Mode",-12} {"Runtime",-10} {"Model",-15} {"Status"}");
            Console.WriteLine(new string('-', 76));
            foreach (var a in agents)
            {
                var model = a.Model?.ToString() ?? "-";
                var status = a.Disabled ? "Disabled" : "Active";
                Console.WriteLine($"{a.Name,-20} {a.Mode,-12} {a.Runtime,-10} {model,-15} {status}");
            }
        });

        var showNameArg = new Argument<string>("name", "代理名称");
        var showCommand = new Command("show", "查看代理详情") { showNameArg };
        showCommand.SetHandler(async (name) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var registry = host.Services.GetRequiredService<IAgentRegistry>();
            var agent = await registry.GetAgentAsync(name);

            if (agent == null)
            {
                Console.Error.WriteLine($"未找到代理: {name}");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"代理: {agent.Name}");
            Console.WriteLine(new string('-', 50));
            Console.WriteLine($"  描述:         {agent.Description ?? "-"}");
            Console.WriteLine($"  模式:         {agent.Mode}");
            Console.WriteLine($"  运行时:       {agent.Runtime}");
            Console.WriteLine($"  模型:         {agent.Model?.ToString() ?? "-"}");
            Console.WriteLine($"  状态:         {(agent.Disabled ? "Disabled" : "Active")}");
            Console.WriteLine($"  最大步数:     {agent.MaxSteps?.ToString() ?? "-"}");
            Console.WriteLine($"  ACP 后端:     {agent.AcpBackend ?? "-"}");
            Console.WriteLine($"  允许的工具:   {(agent.AllowedTools.Count > 0 ? string.Join(", ", agent.AllowedTools) : "(全部)")}");
            Console.WriteLine($"  禁止的工具:   {(agent.DeniedTools.Count > 0 ? string.Join(", ", agent.DeniedTools) : "(无)")}");
            Console.WriteLine($"  权限规则数:   {agent.PermissionRules.Count}");
            Console.WriteLine($"  系统提示词:   {(string.IsNullOrEmpty(agent.SystemPrompt) ? "(无)" : $"{agent.SystemPrompt[..Math.Min(100, agent.SystemPrompt.Length)]}...")}");
        }, showNameArg);

        command.AddCommand(listCommand);
        command.AddCommand(showCommand);

        return command;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Commands/AgentCommand.cs
git commit -m "feat: add agent list/show commands"
```

---

### Task 12: Implement JobCommand

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/JobCommand.cs`

- [ ] **Step 1: Create JobCommand**

Create `samples/Seeing.Agent.Cli/Commands/JobCommand.cs`:

```csharp
using System.CommandLine;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Agent.Scheduler.Abstractions;

namespace Seeing.Agent.Cli.Commands;

public static class JobCommand
{
    public static Command Create()
    {
        var command = new Command("job", "管理定时任务");

        var listCommand = new Command("list", "列出所有定时任务");
        listCommand.SetHandler(async () =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            var statuses = await manager.GetAllJobStatusesAsync();

            Console.WriteLine($"{"JobId",-25} {"State",-12} {"NextRun",-22} {"LastRun",-22}");
            Console.WriteLine(new string('-', 80));
            foreach (var s in statuses)
            {
                var next = s.NextFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                var prev = s.PreviousFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
                Console.WriteLine($"{s.JobId,-25} {s.State,-12} {next,-22} {prev,-22}");
            }
        });

        var runJobArg = new Argument<string>("jobId", "任务 ID");
        var runCommand = new Command("run", "立即执行一个任务") { runJobArg };
        runCommand.SetHandler(async (jobId) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            var result = await manager.RunJobOnceAsync(jobId);

            Console.WriteLine(result switch
            {
                TriggerResult.Accepted a => $"任务已触发，RunId: {a.RunId}",
                TriggerResult.NotFound => "任务未找到",
                TriggerResult.Disabled => "任务已禁用",
                TriggerResult.Conflict c => $"冲突: {c.Reason}",
                _ => "未知结果"
            });
        }, runJobArg);

        var disableJobArg = new Argument<string>("jobId", "任务 ID");
        var disableCommand = new Command("disable", "暂停任务") { disableJobArg };
        disableCommand.SetHandler(async (jobId) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            await manager.DisableJobAsync(jobId);
            Console.WriteLine($"任务 {jobId} 已禁用");
        }, disableJobArg);

        var enableJobArg = new Argument<string>("jobId", "任务 ID");
        var enableCommand = new Command("enable", "恢复任务") { enableJobArg };
        enableCommand.SetHandler(async (jobId) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<IScheduleManager>();
            await manager.ResumeJobAsync(jobId);
            Console.WriteLine($"任务 {jobId} 已恢复");
        }, enableJobArg);

        command.AddCommand(listCommand);
        command.AddCommand(runCommand);
        command.AddCommand(disableCommand);
        command.AddCommand(enableCommand);

        return command;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Commands/JobCommand.cs
git commit -m "feat: add job list/run/disable/enable commands"
```

---

### Task 13: Implement SessionCommand

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/SessionCommand.cs`

- [ ] **Step 1: Create SessionCommand**

Create `samples/Seeing.Agent.Cli/Commands/SessionCommand.cs`:

```csharp
using System.CommandLine;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Session.Core;

namespace Seeing.Agent.Cli.Commands;

public static class SessionCommand
{
    public static Command Create()
    {
        var command = new Command("session", "管理会话");

        var listCommand = new Command("list", "列出所有会话");
        listCommand.SetHandler(async () =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<ISessionManager>();
            var sessions = manager.List();

            Console.WriteLine($"{"SessionId",-38} {"Agent",-20} {"Messages",-10} {"Updated"}");
            Console.WriteLine(new string('-', 92));
            foreach (var s in sessions.OrderByDescending(s => s.UpdatedAt))
            {
                Console.WriteLine($"{s.Id,-38} {s.SelectedAgent ?? "-",-20} {s.Messages.Count,-10} {s.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            }

            if (sessions.Count == 0)
                Console.WriteLine("(无会话)");
        });

        var deleteIdArg = new Argument<string>("id", "会话 ID");
        var deleteCommand = new Command("delete", "删除会话") { deleteIdArg };
        deleteCommand.SetHandler(async (id) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<ISessionManager>();
            var deleted = manager.Delete(id);

            if (deleted)
                Console.WriteLine($"会话 {id} 已删除");
            else
                Console.WriteLine($"未找到会话: {id}");
        }, deleteIdArg);

        var showIdArg = new Argument<string>("id", "会话 ID");
        var showCommand = new Command("show", "查看会话详情") { showIdArg };
        showCommand.SetHandler(async (id) =>
        {
            await using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var manager = host.Services.GetRequiredService<ISessionManager>();
            var session = manager.Get(id);

            if (session == null)
            {
                Console.Error.WriteLine($"未找到会话: {id}");
                Environment.ExitCode = 1;
                return;
            }

            Console.WriteLine($"会话: {session.Id}");
            Console.WriteLine(new string('-', 50));
            Console.WriteLine($"  代理:       {session.SelectedAgent ?? "-"}");
            Console.WriteLine($"  消息数:     {session.Messages.Count}");
            Console.WriteLine($"  创建时间:   {session.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  更新时间:   {session.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  分区:       {session.PartitionId ?? "-"}");
            Console.WriteLine($"  父会话:     {session.ParentSessionId ?? "-"}");
        }, showIdArg);

        command.AddCommand(listCommand);
        command.AddCommand(deleteCommand);
        command.AddCommand(showCommand);

        return command;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build samples/Seeing.Agent.Cli`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add samples/Seeing.Agent.Cli/Commands/SessionCommand.cs
git commit -m "feat: add session list/delete/show commands"
```

---

### Task 14: End-to-end build verification

**Files:** (none new)

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: ALL projects build successfully.

- [ ] **Step 2: Quick smoke test — CLI help**

Run: `dotnet run --project samples/Seeing.Agent.Cli -- --help`
Expected: Shows help with start, stop, status, config, agent, job, session subcommands.

- [ ] **Step 3: Smoke test — config init**

Run:
```bash
mkdir -p /tmp/cli-test && cd /tmp/cli-test
dotnet run --project D:/Old_Project/MyProjects/Seeing.Agent/samples/Seeing.Agent.Cli -- config init
```
Expected: Creates `.seeing/` directory with `seeing.json`, `agents/`, `sessions/`.

- [ ] **Step 4: Cleanup and commit**

```bash
rm -rf /tmp/cli-test
git add .
git commit -m "feat: complete Seeing.Agent.Cli implementation"
```
