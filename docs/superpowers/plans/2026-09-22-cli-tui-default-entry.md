# CLI 默认进入 TUI + WebUI 前台启动 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `seeing-cli` 无参数或 `tui` 子命令时在当前终端原地启动 TUI；`web`/`start webui` 默认改为在 CLI 终端前台启动（新增 `--background` 回退旧行为、`--foreground` 供 gateway 选用）。

**Architecture:** 复用既有「CLI = 服务启动器」模式：TUI/WebUI 均以子进程启动，但新增**前台模式**——子进程继承当前终端（不重定向、不新建窗口），父进程拦截自身 Ctrl+C（`e.Cancel = true`）并把终止权交给子进程，随后阻塞等待其退出。TUI 走新 `TuiCommand`（dll `seeing-tui.dll`）；WebUI/Gateway 前台走 `ServiceProcessManager.StartForeground`。纯逻辑（参数转发、模式解析、启动信息构造）抽成可测静态函数。

**Tech Stack:** .NET 10、System.CommandLine 2.0.11、xUnit 2.9 + FluentAssertions 6.12、MSBuild（`.csproj` 发布目标）。

**关键约束（来自 AGENTS.md / 既有约定）：**
- CLI 不引用 TUI/WebUI 项目（无代码依赖）；TUI 通过**发布目标**把 `seeing-tui` 发布产物拷进 CLI 发布目录（与 `PublishWebUIForCli` 同构，避免 `seeing-tui.runtimeconfig.json` 缺失）。
- 工作目录 = CLI 启动目录（`ResolveWorkspaceRoot()`：`SEEING_WORKSPACE_ROOT` 优先，否则 `Directory.GetCurrentDirectory()`），同时写入子进程 `SEEING_WORKSPACE_ROOT`。
- 禁止 `dotnet run` 启动 TUI（阻塞）；所有验证用 `dotnet build` + `dotnet vstest`。

---

### Task 1: 纯逻辑——运行模式解析与 TUI 参数转发

**Files:**
- Create: `samples/Seeing.Agent.Cli/Services/ServiceRunMode.cs`
- Create: `samples/Seeing.Agent.Cli/Services/TuiLaunch.cs`
- Test: `tests/apps/Seeing.Agent.Cli.Tests/ServiceRunModeResolverTests.cs`
- Test: `tests/apps/Seeing.Agent.Cli.Tests/TuiLaunchTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/apps/Seeing.Agent.Cli.Tests/ServiceRunModeResolverTests.cs`
```csharp
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class ServiceRunModeResolverTests
{
    [Fact]
    public void DefaultFor_WebUi_ShouldBeForeground()
    {
        ServiceRunModeResolver.DefaultFor("webui").Should().Be(ServiceRunMode.Foreground);
    }

    [Fact]
    public void DefaultFor_Gateway_ShouldBeBackground()
    {
        ServiceRunModeResolver.DefaultFor("gateway").Should().Be(ServiceRunMode.Background);
    }

    [Fact]
    public void TryResolve_BackgroundSwitch_ShouldOverrideWebUiDefault()
    {
        ServiceRunModeResolver.TryResolve("webui", background: true, foreground: false, out var mode, out var error)
            .Should().BeTrue();
        mode.Should().Be(ServiceRunMode.Background);
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolve_ForegroundSwitch_ShouldOverrideGatewayDefault()
    {
        ServiceRunModeResolver.TryResolve("gateway", background: false, foreground: true, out var mode, out var error)
            .Should().BeTrue();
        mode.Should().Be(ServiceRunMode.Foreground);
        error.Should().BeNull();
    }

    [Fact]
    public void TryResolve_BothSwitches_ShouldFail()
    {
        ServiceRunModeResolver.TryResolve("webui", background: true, foreground: true, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("不能同时");
    }
}
```

`tests/apps/Seeing.Agent.Cli.Tests/TuiLaunchTests.cs`
```csharp
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class TuiLaunchTests
{
    [Fact]
    public void BuildArguments_WithAllOptions_ShouldEmitPairsInOrder()
    {
        var args = TuiLaunch.BuildArguments(new TuiForwardOptions(
            Continue: true,
            Resume: "ses_1",
            Agent: "plan",
            Model: "gpt-x",
            LogLevel: "Debug",
            Boot: "minimal"));

        args.Should().Equal(
            "--continue", "--resume", "ses_1", "--agent", "plan",
            "--model", "gpt-x", "--log-level", "Debug", "--boot", "minimal");
    }

    [Fact]
    public void BuildArguments_WithoutOptions_ShouldBeEmpty()
    {
        TuiLaunch.BuildArguments(new TuiForwardOptions()).Should().BeEmpty();
    }

    [Fact]
    public void BuildArguments_WithBlankValues_ShouldSkipThem()
    {
        TuiLaunch.BuildArguments(new TuiForwardOptions(Agent: "  ", Model: "")).Should().BeEmpty();
    }

    [Fact]
    public void BuildArguments_ShouldTrimValues()
    {
        TuiLaunch.BuildArguments(new TuiForwardOptions(Resume: " ses_1 "))
            .Should().Equal("--resume", "ses_1");
    }

    [Fact]
    public void BuildEnvironment_ShouldPinWorkspaceRoot()
    {
        var env = TuiLaunch.BuildEnvironment(@"D:\work");

        env["SEEING_WORKSPACE_ROOT"].Should().Be(@"D:\work");
    }

    [Fact]
    public void CanRunInteractive_WhenOnlyInputRedirected_ShouldBeFalse()
    {
        TuiLaunch.CanRunInteractive(inputRedirected: true, outputRedirected: false).Should().BeFalse();
    }

    [Fact]
    public void CanRunInteractive_WhenTerminal_ShouldBeTrue()
    {
        TuiLaunch.CanRunInteractive(inputRedirected: false, outputRedirected: false).Should().BeTrue();
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.Cli.Tests`
Expected: FAIL（`ServiceRunModeResolver` / `TuiLaunch` / `TuiForwardOptions` 不存在，CS0246）

- [ ] **Step 3: 实现**

`samples/Seeing.Agent.Cli/Services/ServiceRunMode.cs`
```csharp
namespace Seeing.Agent.Cli.Services;

/// <summary>
/// 服务进程启动模式：前台占用当前终端（子进程继承控制台），
/// 或后台脱离终端（重定向输出到日志文件）。
/// </summary>
public enum ServiceRunMode
{
    Foreground,
    Background,
}

public static class ServiceRunModeResolver
{
    /// <summary>
    /// 解析启动模式。未显式指定开关时取服务默认值：
    /// WebUI 默认前台（在 CLI 终端直接可见输出）；Gateway 默认后台（保持既有行为）。
    /// </summary>
    public static ServiceRunMode DefaultFor(string service)
        => service == "webui" ? ServiceRunMode.Foreground : ServiceRunMode.Background;

    public static bool TryResolve(
        string service,
        bool background,
        bool foreground,
        out ServiceRunMode mode,
        out string? error)
    {
        error = null;
        if (background && foreground)
        {
            mode = DefaultFor(service);
            error = "不能同时指定 --background 与 --foreground";
            return false;
        }

        mode = background
            ? ServiceRunMode.Background
            : foreground
                ? ServiceRunMode.Foreground
                : DefaultFor(service);
        return true;
    }
}
```

`samples/Seeing.Agent.Cli/Services/TuiLaunch.cs`
```csharp
namespace Seeing.Agent.Cli.Services;

/// <summary>转发给 seeing-tui 的启动选项（仅透传显式指定项，未指定时保留 TUI 自身默认）。</summary>
/// <param name="Continue">继续最近的会话</param>
/// <param name="Resume">恢复指定会话 Id</param>
/// <param name="Agent">覆盖默认 Agent</param>
/// <param name="Model">覆盖默认模型</param>
/// <param name="LogLevel">日志级别</param>
/// <param name="Boot">进程启动能力集（写入 SEEING_BOOT）</param>
public sealed record TuiForwardOptions(
    bool Continue = false,
    string? Resume = null,
    string? Agent = null,
    string? Model = null,
    string? LogLevel = null,
    string? Boot = null);

/// <summary>TUI 子进程的启动规则：dll 名称、参数与环境。</summary>
internal static class TuiLaunch
{
    /// <summary>与 Seeing.Agent.Tui.csproj 的 AssemblyName 一致。</summary>
    public const string DllName = "seeing-tui.dll";

    public static string[] BuildArguments(TuiForwardOptions options)
    {
        var args = new List<string>();
        if (options.Continue)
            args.Add("--continue");

        AddValue(args, "--resume", options.Resume);
        AddValue(args, "--agent", options.Agent);
        AddValue(args, "--model", options.Model);
        AddValue(args, "--log-level", options.LogLevel);
        AddValue(args, "--boot", options.Boot);

        return args.ToArray();
    }

    public static Dictionary<string, string?> BuildEnvironment(string workspaceRoot)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["SEEING_WORKSPACE_ROOT"] = workspaceRoot,
        };

    /// <summary>
    /// 已被重定向的 stdin 无法为 TUI 提供按键输入，必须回退（打印帮助）而不是挂起等待。
    /// stdout 重定向不影响 TUI 运行，故仅以 stdin 判定。
    /// </summary>
    public static bool CanRunInteractive(bool inputRedirected, bool outputRedirected)
        => !inputRedirected;

    private static void AddValue(List<string> args, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        args.Add(name);
        args.Add(value.Trim());
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll --TestCaseFilter:"FullyQualifiedName~ServiceRunModeResolverTests|FullyQualifiedName~TuiLaunchTests"`
Expected: PASS（12 个）

---

### Task 2: 前台子进程运行器

**Files:**
- Create: `samples/Seeing.Agent.Cli/Services/ForegroundProcessRunner.cs`
- Test: `tests/apps/Seeing.Agent.Cli.Tests/ForegroundProcessRunnerTests.cs`

- [ ] **Step 1: 写失败测试**

`tests/apps/Seeing.Agent.Cli.Tests/ForegroundProcessRunnerTests.cs`
```csharp
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class ForegroundProcessRunnerTests
{
    [Fact]
    public void CreateStartInfo_ShouldInheritConsoleInsteadOfRedirecting()
    {
        var psi = ForegroundProcessRunner.CreateStartInfo(
            @"D:\bin\seeing-tui.dll",
            @"D:\work",
            new[] { "--boot", "minimal" },
            new Dictionary<string, string?> { ["SEEING_WORKSPACE_ROOT"] = @"D:\work" },
            contentRoot: @"D:\bin");

        psi.FileName.Should().Be("dotnet");
        psi.UseShellExecute.Should().BeFalse();
        psi.CreateNoWindow.Should().BeFalse();
        psi.RedirectStandardInput.Should().BeFalse();
        psi.RedirectStandardOutput.Should().BeFalse();
        psi.RedirectStandardError.Should().BeFalse();
        psi.WorkingDirectory.Should().Be(@"D:\work");
        psi.ArgumentList.Should().Equal(@"D:\bin\seeing-tui.dll", "--boot", "minimal");
        psi.Environment["SEEING_WORKSPACE_ROOT"].Should().Be(@"D:\work");
        psi.Environment["ASPNETCORE_CONTENTROOT"].Should().Be(@"D:\bin");
    }

    [Fact]
    public void CreateStartInfo_WithoutContentRoot_ShouldNotSetIt()
    {
        var psi = ForegroundProcessRunner.CreateStartInfo(@"D:\bin\seeing-tui.dll", @"D:\work");

        psi.Environment.ContainsKey("ASPNETCORE_CONTENTROOT").Should().BeFalse();
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.Cli.Tests`
Expected: FAIL（CS0246 `ForegroundProcessRunner`）

- [ ] **Step 3: 实现**

`samples/Seeing.Agent.Cli/Services/ForegroundProcessRunner.cs`
```csharp
using System.Diagnostics;

namespace Seeing.Agent.Cli.Services;

/// <summary>
/// 前台子进程运行器：子进程继承当前终端（不重定向、不新建窗口），
/// 父进程吞掉 Ctrl+C 自身信号、把终止交给子进程，随后阻塞等待其退出。
/// </summary>
internal static class ForegroundProcessRunner
{
    /// <summary>
    /// 构造前台启动信息。与 <see cref="ServiceProcessManager.CreateStartInfo"/> 的区别：
    /// 不重定向三个标准流、不隐藏窗口，使子进程直接占用当前终端。
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(
        string dllPath,
        string workingDirectory,
        IEnumerable<string>? arguments = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? contentRoot = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = workingDirectory,
        };

        psi.ArgumentList.Add(dllPath);
        if (arguments is not null)
        {
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(contentRoot))
            psi.Environment["ASPNETCORE_CONTENTROOT"] = contentRoot;

        if (environment is not null)
        {
            foreach (var entry in environment)
                psi.Environment[entry.Key] = entry.Value;
        }

        return psi;
    }

    /// <summary>
    /// 等待子进程退出并返回其退出码。
    /// Ctrl+C 只拦截父进程自身（<c>e.Cancel = true</c>），子进程仍按自身语义处理该信号/按键。
    /// </summary>
    /// <param name="killOnInterruptAfter">
    /// 收到 Ctrl+C 后等待子进程自行退出的宽限期；超过则强制结束进程树。
    /// 传 <c>null</c> 表示永不代为终止——交互式 TUI 需要自行处理退出确认，父进程不得抢杀。
    /// </param>
    public static async Task<int> RunAsync(
        Process process,
        TimeSpan? killOnInterruptAfter = null,
        CancellationToken ct = default)
    {
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;   // 父进程不退出，由子进程决定何时结束
            interrupted.TrySetResult();
        };

        Console.CancelKeyPress += handler;
        try
        {
            var exitTask = process.WaitForExitAsync(ct);

            if (killOnInterruptAfter is null)
            {
                await exitTask.ConfigureAwait(false);
            }
            else
            {
                var first = await Task.WhenAny(exitTask, interrupted.Task).ConfigureAwait(false);
                if (first == interrupted.Task && !process.HasExited)
                {
                    var grace = await Task.WhenAny(
                        exitTask,
                        Task.Delay(killOnInterruptAfter.Value, ct)).ConfigureAwait(false);
                    if (grace != exitTask && !process.HasExited)
                        TryKill(process);
                }

                await exitTask.ConfigureAwait(false);
            }

            return process.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程已自行退出。
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll --TestCaseFilter:"FullyQualifiedName~ForegroundProcessRunnerTests"`
Expected: PASS（2 个）

---

### Task 3: `tui` 子命令 + 无参数默认进入 TUI

**Files:**
- Create: `samples/Seeing.Agent.Cli/Commands/TuiCommand.cs`
- Modify: `samples/Seeing.Agent.Cli/Program.cs`
- Modify: `samples/Seeing.Agent.Cli/Commands/StartCommand.cs:190-214`（`ResolveWorkspaceRoot` / `FindDll` 改 `internal static`）
- Test: `tests/apps/Seeing.Agent.Cli.Tests/CliCommandTests.cs`（追加）

- [ ] **Step 1: 写失败测试（追加到既有 `CliCommandTests`）**

```csharp
    [Fact]
    public void CreateTuiCommand_ShouldExposeTuiEntry()
    {
        var command = TuiCommand.Create();

        command.Name.Should().Be("tui");
        command.Options.Select(o => o.Name).Should().Contain(new[] { "continue", "resume", "agent", "model", "log-level", "boot" });
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.Cli.Tests`
Expected: FAIL（CS0246 `TuiCommand`）

- [ ] **Step 3: 实现 `TuiCommand`**

`samples/Seeing.Agent.Cli/Commands/TuiCommand.cs`
```csharp
using System.CommandLine;
using System.Diagnostics;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli.Commands;

public static class TuiCommand
{
    public static Command Create()
    {
        var command = new Command("tui", "在当前终端前台启动 TUI（等价于无参数运行）");

        var continueOption = new Option<bool>("--continue", "-c") { Description = "继续最近的会话" };
        var resumeOption = new Option<string?>("--resume") { Description = "恢复指定会话 Id" };
        var agentOption = new Option<string?>("--agent") { Description = "覆盖默认 Agent" };
        var modelOption = new Option<string?>("--model") { Description = "覆盖默认模型" };
        var logOption = new Option<string?>("--log-level") { Description = "日志级别（Debug/Information/Warning/Error）" };
        var bootOption = new Option<string?>("--boot")
        {
            Description = "进程启动能力集（写入子进程 SEEING_BOOT，覆盖 seeing.json Boot）"
        };

        command.Options.Add(continueOption);
        command.Options.Add(resumeOption);
        command.Options.Add(agentOption);
        command.Options.Add(modelOption);
        command.Options.Add(logOption);
        command.Options.Add(bootOption);

        command.SetAction(async parseResult =>
        {
            var options = new TuiForwardOptions(
                Continue: parseResult.GetValue(continueOption),
                Resume: parseResult.GetValue(resumeOption),
                Agent: parseResult.GetValue(agentOption),
                Model: parseResult.GetValue(modelOption),
                LogLevel: parseResult.GetValue(logOption),
                Boot: parseResult.GetValue(bootOption));

            Environment.ExitCode = await ExecuteAsync(options);
        });

        return command;
    }

    /// <summary>
    /// 在当前终端原地启动 seeing-tui：子进程继承控制台，工作目录与工作区均为 CLI 启动目录。
    /// TUI 自行处理 Ctrl+C 与退出确认，故父进程不设宽限期强制终止。
    /// </summary>
    internal static async Task<int> ExecuteAsync(TuiForwardOptions options, CancellationToken ct = default)
    {
        if (!TuiLaunch.CanRunInteractive(Console.IsInputRedirected, Console.IsOutputRedirected))
        {
            Console.Error.WriteLine("当前 stdin 非交互，无法启动 TUI；请改用 seeing-cli --help 查看命令。");
            return 1;
        }

        string dllPath;
        try
        {
            dllPath = StartCommand.FindDll(AppDomain.CurrentDomain.BaseDirectory, TuiLaunch.DllName);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var workspaceRoot = StartCommand.ResolveWorkspaceRoot();
        var psi = ForegroundProcessRunner.CreateStartInfo(
            dllPath,
            workspaceRoot,
            TuiLaunch.BuildArguments(options),
            TuiLaunch.BuildEnvironment(workspaceRoot),
            contentRoot: Path.GetDirectoryName(dllPath));

        using var process = Process.Start(psi);
        if (process is null)
        {
            Console.Error.WriteLine("无法启动 TUI 进程");
            return 1;
        }

        return await ForegroundProcessRunner.RunAsync(process, killOnInterruptAfter: null, ct);
    }
}
```

- [ ] **Step 4: 修改 `StartCommand` 可见性**

`StartCommand.cs`：`private static string ResolveWorkspaceRoot()` → `internal static string ResolveWorkspaceRoot()`；`private static string FindDll(...)` → `internal static string FindDll(...)`。（行为不变。）

同时 `FindDll` 的仓库回退路径已按 `samples/<AssemblyName>/bin/Debug/net10.0/<dllName>` 拼装，传 `seeing-tui.dll` 即命中 `samples/Seeing.Agent.Tui/bin/Debug/net10.0/seeing-tui.dll`，无需改动。

- [ ] **Step 5: 修改 `Program.cs`**

```csharp
using System.CommandLine;
using Seeing.Agent.Cli.Commands;
using Seeing.Agent.Cli.Services;

namespace Seeing.Agent.Cli;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        // 无参数 = 在启动目录原地进入 TUI；`tui` / `start tui` 为等价的显式入口。
        if (args.Length == 0)
            return await TuiCommand.ExecuteAsync(new TuiForwardOptions());

        var rootCommand = new RootCommand("Seeing.Agent CLI 管理工具");

        rootCommand.Subcommands.Add(InstallCommand.Create());
        rootCommand.Subcommands.Add(StartCommand.Create());
        rootCommand.Subcommands.Add(StartCommand.CreateWeb());
        rootCommand.Subcommands.Add(StartCommand.CreateGateway());
        rootCommand.Subcommands.Add(TuiCommand.Create());
        rootCommand.Subcommands.Add(StopCommand.Create());
        rootCommand.Subcommands.Add(StatusCommand.Create());
        rootCommand.Subcommands.Add(ConfigCommand.Create());
        rootCommand.Subcommands.Add(AgentCommand.Create());
        rootCommand.Subcommands.Add(JobCommand.Create());
        rootCommand.Subcommands.Add(SessionCommand.Create());

        return await rootCommand.Parse(args).InvokeAsync();
    }
}
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet build Seeing.Agent.slnx` → 0 错误
Run: `dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll --TestCaseFilter:"FullyQualifiedName~CliCommandTests"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add samples/Seeing.Agent.Cli tests/apps/Seeing.Agent.Cli.Tests
git commit -m "feat(cli): launch TUI in place on no args or 'tui' subcommand"
```

---

### Task 4: WebUI/Gateway 前台启动模式

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Services/ServiceProcessManager.cs`（新增 `StartForeground`）
- Modify: `samples/Seeing.Agent.Cli/Commands/StartCommand.cs`
- Test: `tests/apps/Seeing.Agent.Cli.Tests/CliCommandTests.cs`（追加）

- [ ] **Step 1: 写失败测试（追加）**

```csharp
    [Fact]
    public void CreateWeb_ShouldExposeRunModeSwitches()
    {
        var options = StartCommand.CreateWeb().Options.Select(o => o.Name).ToList();

        options.Should().Contain("background");
        options.Should().Contain("foreground");
    }

    [Fact]
    public void Create_ShouldAcceptTuiAsService()
    {
        // `start tui` 与 `tui` 等价，由 StartCommand 委派 TuiCommand 执行。
        StartCommand.Create().Name.Should().Be("start");
        StartCommand.SupportedServices.Should().Contain("tui");
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet build tests/apps/Seeing.Agent.Cli.Tests`
Expected: FAIL（`SupportedServices` 不存在 / options 缺 `background`）

- [ ] **Step 3: `ServiceProcessManager` 新增前台启动**

在 `ServiceProcessManager.cs` 的 `StartAsync` 之后追加：
```csharp
    /// <summary>
    /// 前台启动：子进程继承当前终端，不重定向输出（故不写日志文件），
    /// 仍登记实例以便 status 可见、stop（HTTP shutdown）可用。调用方负责退出后 <c>Registry.Remove</c>。
    /// </summary>
    public (Process Process, InstanceRecord Record) StartForeground(
        string service,
        string dllPath,
        int port,
        string[]? extraArgs = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"找不到 {service} 的 dll 文件: {dllPath}");

        var contentRoot = Path.GetDirectoryName(dllPath)!;
        var psi = ForegroundProcessRunner.CreateStartInfo(
            dllPath,
            _workspaceRoot,
            extraArgs,
            environment,
            contentRoot);
        psi.Environment["SEEING_WORKSPACE_ROOT"] = _workspaceRoot;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动 {service} 进程");

        var record = new InstanceRecord
        {
            Id = $"{service}-{Guid.NewGuid().ToString("N")[..6]}",
            Service = service,
            Pid = process.Id,
            WorkspaceRoot = _workspaceRoot,
            Port = port,
            StartedAt = DateTime.UtcNow,
            LogPath = Path.Combine(
                _workspaceRoot, ".seeing", "logs", $"{service}-foreground.log"),
        };

        Registry.Add(record);
        return (process, record);
    }
```

- [ ] **Step 4: 改写 `StartCommand`**

要点（完整文件）：
```csharp
using System.CommandLine;
using System.Diagnostics;
using System.Linq;
using Seeing.Agent.Cli.Services;
using Seeing.Agent.Core.Modules;

namespace Seeing.Agent.Cli.Commands;

public static class StartCommand
{
    /// <summary>受支持的启动目标；tui 委派给 <see cref="TuiCommand"/>。</summary>
    public static readonly string[] SupportedServices = { "webui", "gateway", "tui" };

    public static Command Create()
        => CreateCommand("start", "启动指定的服务", fixedService: null);

    public static Command CreateWeb()
        => CreateCommand("web", "在前台启动 WebUI 并打开浏览器（--background 可改为后台）", "webui");

    public static Command CreateGateway()
        => CreateCommand("gateway", "启动 Gateway（默认后台，--foreground 可占用当前终端）", "gateway");

    private static Command CreateCommand(string commandName, string description, string? fixedService)
    {
        var command = new Command(commandName, description);
        Argument<string>? serviceArg = null;
        if (fixedService is null)
        {
            serviceArg = new Argument<string>("service")
            {
                Description = "要启动的服务: webui、gateway 或 tui"
            };
            command = new Command(commandName, description) { serviceArg };
        }

        var bootOption = new Option<string?>("--boot")
        {
            Description =
                "进程启动能力集（写入子进程 SEEING_BOOT，覆盖 seeing.json Boot）。" +
                "内置：* / minimal / code / work / research / full / secure / dev"
        };
        var backgroundOption = new Option<bool>("--background", "-b")
        {
            Description = "后台启动（脱离当前终端，输出写入日志文件）"
        };
        var foregroundOption = new Option<bool>("--foreground", "-f")
        {
            Description = "前台启动（占用当前终端，Ctrl+C 停止）"
        };
        command.Options.Add(bootOption);
        command.Options.Add(backgroundOption);
        command.Options.Add(foregroundOption);

        command.SetAction(async parseResult =>
        {
            var service = (fixedService ?? parseResult.GetValue(serviceArg!)) ?? string.Empty;
            var boot = parseResult.GetValue(bootOption);
            var background = parseResult.GetValue(backgroundOption);
            var foreground = parseResult.GetValue(foregroundOption);
            await ExecuteStartAsync(service.ToLowerInvariant(), boot, background, foreground);
        });

        return command;
    }

    private static async Task ExecuteStartAsync(
        string service,
        string? boot,
        bool background,
        bool foreground)
    {
        if (!SupportedServices.Contains(service))
        {
            Console.Error.WriteLine("错误: service 必须是 'webui'、'gateway' 或 'tui'");
            Environment.ExitCode = 1;
            return;
        }

        var effectiveBoot = string.IsNullOrWhiteSpace(boot)
            ? BootOverrideSource.ResolveFromEnvironment()
            : boot.Trim();

        if (service == "tui")
        {
            Environment.ExitCode = await TuiCommand.ExecuteAsync(
                new TuiForwardOptions(Boot: string.IsNullOrWhiteSpace(effectiveBoot) ? null : effectiveBoot));
            return;
        }

        if (!ServiceRunModeResolver.TryResolve(service, background, foreground, out var mode, out var modeError))
        {
            Console.Error.WriteLine($"错误: {modeError}");
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
                if (service == "webui")
                    OpenWebUi(WebUiLaunch.BuildUrl(running[0].Port));
                return;
            }

            var cliDir = AppDomain.CurrentDomain.BaseDirectory;
            string dllName = service == "webui"
                ? "Seeing.Agent.WebUI.dll"
                : "Seeing.Gateway.Server.dll";
            var dllPath = FindDll(cliDir, dllName);

            var webUiAllocator = service == "webui" ? new PortAllocator() : null;
            var nextWebUiPort = WebUiLaunch.PreferredPort;
            var webUiRetryCount = 0;
            var port = 0;
            InstanceRecord? record = null;
            Process? foregroundProcess = null;
            var foregroundRecord = default(InstanceRecord);

            while (true)
            {
                port = service == "webui"
                    ? webUiAllocator!.NextAvailable(nextWebUiPort)
                    : GetGatewayPort(workspaceRoot);
                var launchUrl = service == "webui" ? WebUiLaunch.BuildUrl(port) : null;
                var extraArgs = service == "webui"
                    ? WebUiLaunch.BuildArguments(port)
                    : Array.Empty<string>();
                var launchEnvironment = service == "webui"
                    ? WebUiLaunch.BuildEnvironment(port)
                    : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(effectiveBoot))
                {
                    launchEnvironment[BootOverrideSource.EnvironmentVariableName] = effectiveBoot;
                    Console.WriteLine($"BootOverride={effectiveBoot}（{BootOverrideSource.EnvironmentVariableName}）");
                }

                Console.WriteLine($"正在{(mode == ServiceRunMode.Foreground ? "前台" : "后台")}启动 {service}（端口: {port}）...");

                if (mode == ServiceRunMode.Foreground)
                {
                    (foregroundProcess, foregroundRecord) = manager.StartForeground(
                        service, dllPath, port, extraArgs, launchEnvironment);
                    record = foregroundRecord;
                }
                else
                {
                    record = await manager.StartAsync(
                        service, dllPath, port, extraArgs, environment: launchEnvironment);
                }

                Console.WriteLine($"{service} 进程已启动（PID: {record.Pid}）{(mode == ServiceRunMode.Foreground ? string.Empty : $"，日志: {record.LogPath}")}");

                using var apiClient = new ManagementApiClient(launchUrl ?? $"http://127.0.0.1:{port}");
                Console.Write($"等待 {service} 就绪");
                try
                {
                    await manager.WaitForReadyAsync(
                        apiClient,
                        checkGatewayHealth: service == "gateway",
                        process: record);
                    Console.WriteLine(" 就绪!");
                    break;
                }
                catch
                {
                    var portCollision = service == "webui"
                        && webUiRetryCount < 3
                        && !manager.IsProcessRunning(record)
                        && !webUiAllocator!.IsAvailable(port);

                    await manager.StopAsync(record, apiClient, CancellationToken.None);
                    if (mode == ServiceRunMode.Foreground)
                        foregroundProcess?.Dispose();

                    if (!portCollision)
                        throw;

                    webUiRetryCount++;
                    nextWebUiPort = port + 1;
                    Console.WriteLine($"端口 {port} 绑定失败，详见日志: {record.LogPath}；改用端口 {nextWebUiPort} 重试...");
                }
            }

            if (record is null)
                throw new InvalidOperationException("服务进程未成功启动");

            if (service == "webui")
            {
                var url = WebUiLaunch.BuildUrl(port);
                Console.WriteLine($"WebUI 已启动: {url}（工作区: {workspaceRoot}）");
                OpenWebUi(url);
            }
            else
            {
                Console.WriteLine($"Gateway 已启动: http://127.0.0.1:{port}（工作区: {workspaceRoot}）");
            }

            if (mode != ServiceRunMode.Foreground || foregroundProcess is null)
                return;

            Console.WriteLine("前台运行中，按 Ctrl+C 停止...");
            try
            {
                await ForegroundProcessRunner.RunAsync(
                    foregroundProcess,
                    killOnInterruptAfter: TimeSpan.FromSeconds(10));
            }
            finally
            {
                manager.Registry.Remove(record.Id);
                foregroundProcess.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void OpenWebUi(string url) { /* 保持原实现不变 */ }

    internal static string ResolveWorkspaceRoot() { /* 保持原实现 */ }

    internal static string FindDll(string cliDir, string dllName) { /* 保持原实现 */ }

    private static int GetGatewayPort(string workspaceRoot) { /* 保持原实现 */ }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet build Seeing.Agent.slnx` → 0 错误
Run: `dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll`
Expected: 全部 PASS

- [ ] **Step 6: Commit**

```bash
git add samples/Seeing.Agent.Cli tests/apps/Seeing.Agent.Cli.Tests
git commit -m "feat(cli): run webui in the foreground by default, gateway opt-in"
```

---

### Task 5: 发布目标——把 seeing-tui 产物并入 CLI 发布目录

**Files:**
- Modify: `samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj`

- [ ] **Step 1: 追加发布目标**

在既有 `PublishWebUIForCli` 目标之后追加同构目标（独立 staging、独立 manifest、`dotnet publish` 子进程避开 VS 增量缓存）：
```xml
  <!--
    TUI 解耦发布：与 WebUI 同因（Cli 不引用 Tui 项目）。
    必须拷贝整份发布产物（含 seeing-tui.runtimeconfig.json / apphost），
    否则 `dotnet seeing-tui.dll` 会因缺少 runtimeconfig 启动失败。
  -->
  <PropertyGroup>
    <TuiPublishStagingDir>$(MSBuildProjectDirectory)\obj\$(Configuration)\$(TargetFramework)\tui-publish</TuiPublishStagingDir>
    <TuiPublishManifest>$(PublishDir)\.seeing-tui-files.txt</TuiPublishManifest>
  </PropertyGroup>

  <Target Name="PublishTuiForCli" AfterTargets="Publish" Condition="'$(PublishDir)' != ''">
    <RemoveDir Directories="$(TuiPublishStagingDir)" Condition="Exists('$(TuiPublishStagingDir)')" />
    <MakeDir Directories="$(TuiPublishStagingDir)" />

    <Exec Command="set &quot;DOTNET_CLI_UI_LANGUAGE=en&quot; &amp;&amp; dotnet publish &quot;$(MSBuildThisFileDirectory)../Seeing.Agent.Tui/Seeing.Agent.Tui.csproj&quot; -c $(Configuration) -o &quot;$(TuiPublishStagingDir)&quot; -p:SelfContained=false --nologo"
          StandardOutputImportance="high" />

    <ItemGroup>
      <TuiPublishFiles Include="$(TuiPublishStagingDir)\**\*" />
    </ItemGroup>

    <ReadLinesFromFile File="$(TuiPublishManifest)" Condition="Exists('$(TuiPublishManifest)')">
      <Output TaskParameter="Lines" ItemName="PreviousTuiFiles" />
    </ReadLinesFromFile>
    <Delete Files="@(PreviousTuiFiles->'$(PublishDir)%(Identity)')"
            Condition="@(PreviousTuiFiles) != ''" />

    <Copy SourceFiles="@(TuiPublishFiles)"
          DestinationFiles="@(TuiPublishFiles->'$(PublishDir)%(RecursiveDir)%(Filename)%(Extension)')"
          SkipUnchangedFiles="false"
          OverwriteReadOnlyFiles="true" />

    <WriteLinesToFile File="$(TuiPublishManifest)"
                      Lines="@(TuiPublishFiles->'%(RecursiveDir)%(Filename)%(Extension)')"
                      Overwrite="true"
                      Encoding="UTF-8" />
  </Target>
```

- [ ] **Step 2: 验证发布产物包含 TUI**

Run: `dotnet publish samples/Seeing.Agent.Cli -c Debug -o "$env:TEMP\seeing-cli-publish"`
Expected: 输出目录同时存在 `seeing-cli.dll`、`Seeing.Agent.WebUI.dll`、`seeing-tui.dll`、`seeing-tui.runtimeconfig.json`

- [ ] **Step 3: 文档同步**

`docs/architecture/02-modules.md` 中 `samples/Seeing.Agent.Cli` 一行改为：`| samples/Seeing.Agent.Cli | CLI 管理工具（无参数 = 前台进入 TUI；web/gateway 可前台/后台） |`

- [ ] **Step 4: Commit**

```bash
git add samples/Seeing.Agent.Cli/Seeing.Agent.Cli.csproj docs/architecture/02-modules.md
git commit -m "build(cli): publish seeing-tui alongside webui for the cli"
```

---

### Task 6: 全量验证

- [ ] **Step 1: 构建**

Run: `dotnet build Seeing.Agent.slnx`
Expected: 0 错误

- [ ] **Step 2: 测试**

Run: `dotnet vstest tests/apps/Seeing.Agent.Cli.Tests/bin/Debug/net10.0/Seeing.Agent.Cli.Tests.dll`
Run: `dotnet vstest tests/apps/Seeing.Agent.Tui.Tests/bin/Debug/net10.0/Seeing.Agent.Tui.Tests.dll`
Run: `dotnet test tests/primitives/Seeing.Session.Tests`
Expected: 全绿

- [ ] **Step 3: 人工验收（需用户在真实终端执行，自动化测试无法覆盖）**

| 命令 | 预期 |
|---|---|
| `dotnet run --project samples/Seeing.Agent.Cli` | 直接进入 TUI；输入正常；Ctrl+C/双击 Esc 退出后回到 shell |
| `dotnet run --project samples/Seeing.Agent.Cli -- tui` | 同上（显式入口） |
| `dotnet run --project samples/Seeing.Agent.Cli -- web` | WebUI 在前台启动，日志直接打印在终端，浏览器自动打开；Ctrl+C 停止 |
| `dotnet run --project samples/Seeing.Agent.Cli -- web --background` | 旧行为：打印就绪后立即返回 shell |
| `dotnet run --project samples/Seeing.Agent.Cli -- gateway --foreground` | Gateway 前台运行，Ctrl+C 停止 |
| 另一终端 `seeing-cli status` / `stop webui` | 能列出/停止前台启动的 WebUI |

---

## 风险与取舍

1. **前台子进程的 Ctrl+C 语义**：父进程仅拦截自身信号（`e.Cancel = true`），不向子进程转发。Windows 下 TUI 处于 raw 模式时 Ctrl+C 被子进程当作按键读取；WebUI/Gateway 未改控制台模式，仍会收到 CTRL_C_EVENT 并优雅关闭。若子进程 10 秒内未退出（仅 WebUI/Gateway），强制结束进程树兜底。
2. **`seeing-tui.dll` 依赖 `runtimeconfig.json`**：必须走发布目标整目录拷贝，不能只做 ProjectReference（后者不复制 runtimeconfig）。
3. **前台 WebUI 仍登记实例**：`LogPath` 指向不存在的 `*-foreground.log`（前台输出直接进终端），`status` 不展示该字段，`stop` 走 HTTP shutdown 仍有效。
4. **`tui` 不登记实例**：交互式会话无需 `stop`/`status` 管理，退出即结束，避免残留脏记录。
5. **无参数 + stdin 重定向**：直接返回错误提示而非挂起，避免脚本/CI 中卡死。

---

## 审查修正（v2，两路 subagent 审查后）

| # | 级别 | 问题 | 修正 |
|---|------|------|------|
| 1 | P0 | `Option.Name` 返回**带前缀**主别名（`--continue`），计划里的断言写成无前缀名 → 测试必失败 | 断言统一 `o.Name.TrimStart('-')`；已在 `CliCommandTests` 追加用例中采用 |
| 2 | P0 | `StartCommand.FindDll` 的仓库回退路径**双重错误**：`..` 只有 4 级（解析为 `samples`，再拼 `samples` → `samples/samples/...`）；且用 `dllName.Replace(".dll","")` 当目录名（`seeing-tui` ≠ `Seeing.Agent.Tui`） | 抽出 `ServiceAssetLocator`：显式「程序集名→项目目录名」映射 + 向上查找 `Seeing.Agent.slnx` 定位仓库根 + 仅扫 `bin` 子目录 |
| 3 | P1 | `TuiLaunch.CanRunInteractive` 只看 stdin，而 TUI 自身 `TuiChatEngine.cs:151` 同时校验 stdout → stdout 重定向时会白起一个必然返回 2 的子进程 | 改为 `!(inputRedirected || outputRedirected)` |
| 4 | P1 | `ASPNETCORE_CONTENTROOT` 对通用 Host（TUI 用 `Host.CreateApplicationBuilder`）无效 | TUI 用 `DOTNET_CONTENTROOT`；WebUI 保持 `ASPNETCORE_CONTENTROOT`。`CreateStartInfo` 不再收 contentRoot 参数，改由环境字典显式携带 |
| 5 | P2 | `SetAction` 无返回值，`Main` 的 `return await InvokeAsync()` 会吞掉 `Environment.ExitCode` | `Program.Main` 改为 `var code = await ...; return Environment.ExitCode != 0 ? Environment.ExitCode : code;` |
| 6 | P2 | `TryKill` 静默失败后 `await exitTask` 可能永久挂起 | `TryKill` 失败时追加有界兜底等待（5s），超时返回 1 并提示手动结束 |
| 7 | 兼容 | TUI 项目无内容文件（无 `appsettings.json`/`wwwroot`），故可安全加**构建期**依赖 | CLI csproj 增加 `ProjectReference ... ReferenceOutputAssembly="false"`：仅确保 `dotnet build` 产出 `seeing-tui.dll`，**不引入代码依赖**、不复制内容文件 |
| 8 | 事实校正 | 计划原称「父进程拦截 Ctrl+C 后把终止权交给子进程」 | 实际 TUI raw 模式清掉 `ENABLE_PROCESSED_INPUT`（Unix 清 `ISIG`），CTRL_C_EVENT/SIGINT 根本不产生，父 handler 基本不触发；该机制只对 WebUI/Gateway 前台生效。TUI 保持 `killOnInterruptAfter: null` |

---

## 实施与验证结果（最终）

**测试**：`dotnet build Seeing.Agent.slnx` → 0 错误；`Seeing.Agent.Cli.Tests` 53 通过；`Seeing.Agent.Tui.Tests` 296 通过（新增 `TuiCliOptionsParserTests` 锁定转发线格式）。

**实机冒烟（本机无 TTY，故用重定向 + 进程观测代替人工终端验收）**：
| 验证项 | 结果 |
|---|---|
| `seeing-cli --help` | 列出 `start <service>` / `web`（“前台启动 WebUI…--background 可改为后台”）/ `tui` / `status` ✅ |
| `seeing-cli tui`（stdin 重定向） | 打印“当前终端非交互…”，**exit=1** ✅（证明 `Main` 不再吞掉 `Environment.ExitCode`） |
| `seeing-cli start tui`（同上） | 同上 ✅ |
| `seeing-tui.dll --continue=true --boot minimal` 直启 | 正常构建 Host 并命中 TUI 自身非交互守卫，exit=2 ✅（转发参数与 Host 配置解析均无异常） |
| `seeing-cli gateway --foreground` | 输出“正在**前台**启动 gateway（端口: 8765）” → 子进程日志**直接打印在继承的终端**、`Content root path` = 启动目录 → 30s 后“未确认 HTTP 就绪（进程仍在运行，继续前台等待）” → “前台运行中，按 Ctrl+C 停止...” → **父进程阻塞等待**（45s 时仍未退出）✅ |
| 杀掉前台子进程 | 父 CLI 随即收尾退出（`HasExited=True`），注册表无残留 ✅ |

**审查后修复的 6 项（第二路实现审查发现）**：
1. `--continue` 改发 `--continue=true`（无值开关会被子进程 Host 的命令行配置提供程序吞掉后继参数）。
2. 前台实例的注册表记录/进程对象清理覆盖所有异常与提前返回路径（`CleanupForeground`）。
3. 前台记录 `LogPath = string.Empty`，提示改用 `DescribeOutput`（避免指向不存在的日志文件）。
4. TUI 路径无条件以 `Directory.GetCurrentDirectory()` 为工作区（严格满足“工作目录 = 启动目录”，并覆盖继承的 `SEEING_WORKSPACE_ROOT`）。
5. 仓库回退查找优先命中与 CLI 相同配置目录、其次取最近构建产物（结果确定，避免陈旧 Release 产物）。
6. 补 `TuiCliOptionsParserTests` 往返测试、`Find_InRepositoryLayout` 在非源码树运行时跳过。

**发现但未修的既存缺陷（与本需求无关，需另行决策）**：
`seeing-cli start/gateway`（含后台模式）的就绪探测请求 `http://127.0.0.1:{port}/api/gateway/health`，而 Gateway 进程启动后既不监听 8765（`Invoke-WebRequest` 连接被拒）、日志也无 `Now listening on`，且 `.seeing/seeing.json` 无 `Gateway` 段（端口回落到 8765）→ 后台模式 30 秒后必然报“服务在 30 秒内未就绪”并停掉刚启动的 Gateway。本次仅让**前台**模式退化为“未确认就绪即继续”，未改后台语义。

---

## 追加修复：`start gateway` 就绪探测必然失败（已修）

**根因（源码证据）**：
1. `GatewayOptions` 经 `ConfigSectionMeta(GatewayOptions.SectionName /*"Gateway"*/, "seeing.json", ConfigScope.ProjectOnly, ...)` 注册（`GatewayServiceCollectionExtensions.cs:33-36`），且 `GatewayOptionsMonitor.Value` 走 `IConfigSectionStore.GetSection<GatewayOptions>("Gateway")`（`GatewayOptionsMonitor.cs:18`）——配置**只来自项目级 `.seeing/seeing.json`**。
2. `AddSeeingGatewayServer(registry, IConfiguration)` 的 configuration 参数被显式忽略（`GatewayServiceCollectionExtensions.cs:61` `_ = configuration;`）→ 环境变量与命令行参数都无法覆盖。
3. `UnifiedConfigManager.LoadAsync` 只合并 user/project 两级 `seeing.json`（`UnifiedConfigManager.cs:77-90`），不读环境变量。
4. `GatewayOptions.Enabled` 默认 **false**（`GatewayOptions.cs:11`），`GatewayHostedService` 与 `GatewayServer` 都以 `Enabled` 为启动门禁（`GatewayHostedService.cs:31`、`GatewayServer.cs:38`）。

→ 默认配置下 Gateway 进程启动后**不监听任何端口**，而 CLI 的就绪探测固定打 `http://127.0.0.1:{port}/api/gateway/health`，30 秒后必然超时并停掉刚启动的进程。

**修复**：新增 `Services/GatewayConfig.cs`，在启动 Gateway 前读项目级 `SeeingAgent:Gateway`（大小写不敏感、容忍注释/尾逗号/损坏 JSON、端口越界回落 8765）：
- `Enabled=false`/缺失 → **立即**打印可照做的指引并以 1 退出（不再空等 30 秒、不再误杀进程）；
- `Enabled=true` → 用配置里的 `Port` 作为启动端口与就绪探测 URL（原先只读 Port，且与运行时同源，现完全一致）。

**验证**：
| 场景 | 结果 |
|---|---|
| 当前仓库配置（无 Gateway 节） | `seeing-cli gateway` → 0.4s 打印“Gateway 未启用：请在 …\.seeing\seeing.json 中设置 …”并 exit=1 ✅ |
| 临时工作区 `{ Enabled: true, Port: 8791 }` | `正在后台启动 gateway（端口: 8791）` → `等待 gateway 就绪 就绪!` → 探测 `8791/api/gateway/health` 返回 **HTTP 200 {"status":"healthy"}** → `seeing-cli stop gateway` 正常停止、健康端点随即关闭、无残留进程 ✅ |

测试：`Seeing.Agent.Cli.Tests` 63 通过（新增 `GatewayConfigTests` 10 例：缺失/启用/禁用/无节/camelCase/端口非法×3/损坏 JSON/指引文案）。
