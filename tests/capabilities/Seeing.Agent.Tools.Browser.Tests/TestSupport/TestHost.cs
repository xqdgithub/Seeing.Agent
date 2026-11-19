using System.Diagnostics;
using System.Globalization;
using System.Text;
using Seeing.Agent.Tools.Browser;

namespace Seeing.Agent.Tools.Browser.Tests;

/// <summary>
/// 真实浏览器集成测试的统一门禁。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认关闭</b>：CI 环境未必装了浏览器，也不该让单元测试套件依赖它。
/// 本机跑：<c>set SEEING_BROWSER_TESTS=1</c> 后再执行。
/// </para>
/// <para>
/// 与 <c>[Trait("Category", "RequiresBrowser")]</c> 配合：trait 让 CI 能整类过滤，
/// 环境变量让默认的 <c>dotnet vstest</c> 不白起浏览器。
/// </para>
/// </remarks>
internal static class BrowserTestGate
{
    /// <summary>是否允许运行真实浏览器用例。</summary>
    public static bool Enabled
        => string.Equals(
            Environment.GetEnvironmentVariable("SEEING_BROWSER_TESTS"), "1", StringComparison.Ordinal);
}

/// <summary>
/// 拉起真实浏览器宿主（<c>dotnet run -- --browser-host ...</c>）并在测试结束时清理。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么返回的是宿主进程而不是 <c>dotnet run</c></b>：<c>dotnet run</c> 只是启动器，
/// 实际宿主是它的子进程。测试要验证「宿主被杀 → 浏览器跟着死」，
/// 杀启动器不会影响子进程；因此这里先从发现文件里读出真实 hostPid，再返回该进程。
/// </para>
/// <para>
/// <b>为什么把发现文件/日志根目录指到 profile</b>：测试绝不能往真实的
/// <c>~/.seeing/browser</c> 写东西，否则会干扰本机正在使用的托管浏览器。
/// 宿主支持 <c>SEEING_BROWSER_BASE_DIR</c> 覆盖，测试与宿主读同一份目录。
/// </para>
/// </remarks>
internal static class TestHost
{
    /// <summary>宿主发现文件根目录覆盖（与 BrowserHostRunner 保持一致）。</summary>
    private const string BaseDirectoryVariable = "SEEING_BROWSER_BASE_DIR";

    /// <summary>轮询间隔（E5：实现细节，命中即返回）。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>已构建产物启动宿主的上限（启动浏览器本身约 1–10 秒）。</summary>
    private static readonly TimeSpan NoBuildTimeout = TimeSpan.FromSeconds(30);

    /// <summary>回落「带构建」启动时的上限（首次可能需要编译）。</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);

    /// <summary>等进程消失的上限。</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    private static readonly object s_gate = new();

    /// <summary>每份 profile 对应的 <c>dotnet run</c> 启动器进程（清理时一并收起）。</summary>
    private static readonly Dictionary<string, Process> s_launchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>启动器输出（诊断「为什么没起来」用）。</summary>
    private static readonly Dictionary<string, StringBuilder> s_launcherOutput = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 启动宿主并等它写出发现文件；返回**真实宿主进程**。
    /// </summary>
    /// <param name="profileDirectory">专用 profile 目录（同时作为发现文件根目录）。</param>
    /// <param name="idleSeconds">宿主空闲退出秒数（测试用较短值）。</param>
    /// <exception cref="InvalidOperationException">宿主在超时内没有发布发现文件。</exception>
    public static Process Start(string profileDirectory, int idleSeconds = 300)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);

        Directory.CreateDirectory(profileDirectory);
        var locator = new BrowserHostLocator(profileDirectory, profileDirectory);

        var launcher = StartLauncher(locator.ProfileDirectory, idleSeconds, useNoBuild: true);
        var info = WaitForDiscovery(locator, launcher, NoBuildTimeout);

        if (info is null)
        {
            // `--no-build` 在「还没构建过 CLI」时会立刻失败：这种情况回落到允许构建。
            if (!launcher.HasExited)
                KillQuietly(launcher);

            launcher.Dispose();
            launcher = StartLauncher(locator.ProfileDirectory, idleSeconds, useNoBuild: false);
            info = WaitForDiscovery(locator, launcher, BuildTimeout);
        }

        if (info is null)
        {
            var output = ReadLauncherOutput(locator.ProfileDirectory);
            KillQuietly(launcher);
            launcher.Dispose();
            throw new InvalidOperationException(
                $"浏览器宿主未在超时内发布发现文件（profile={locator.ProfileDirectory}）。" +
                $"启动器输出：{output}");
        }

        lock (s_gate)
            s_launchers[locator.ProfileDirectory] = launcher;

        return Process.GetProcessById(info.HostPid);
    }

    /// <summary>
    /// 杀掉宿主与其启动器，并等进程真正消失（条件轮询，不猜时间）。
    /// </summary>
    /// <param name="profileDirectory">启动时使用的 profile 目录。</param>
    public static void Cleanup(string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
            return;

        string profile;
        try
        {
            profile = BrowserHostLocator.NormalizeProfileDirectory(profileDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        var locator = new BrowserHostLocator(profile, profile);
        var info = locator.Read();

        if (info is not null)
            KillQuietly(info.HostPid);

        Process? launcher;
        lock (s_gate)
        {
            s_launchers.Remove(profile, out launcher);
            s_launcherOutput.Remove(profile);
        }

        if (launcher is not null)
        {
            KillQuietly(launcher);
            launcher.Dispose();
        }

        if (info is not null)
            WaitUntilGone(info.HostPid, ExitTimeout);

        // 宿主被强杀时不会走 finally：留下的是过期发现文件，删掉避免污染下一次启动。
        if (info?.HostPid > 0 && !IsProcessAlive(info.HostPid))
            TryDelete(locator.InfoPath);
    }

    /// <summary>启动 <c>dotnet run --project samples/Seeing.Agent.Cli -- --browser-host ...</c>。</summary>
    private static Process StartLauncher(string profileDirectory, int idleSeconds, bool useNoBuild)
    {
        var projectPath = Path.Combine(FindRepositoryRoot(), "samples", "Seeing.Agent.Cli", "Seeing.Agent.Cli.csproj");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // 测试隔离：发现文件 / 客户端锁 / 日志都落在本次 profile 里。
            Environment = { [BaseDirectoryVariable] = profileDirectory },
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        if (useNoBuild)
            startInfo.ArgumentList.Add("--no-build");

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--browser-host");
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(profileDirectory);
        startInfo.ArgumentList.Add("--idle-seconds");
        startInfo.ArgumentList.Add(idleSeconds.ToString(CultureInfo.InvariantCulture));

        var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 dotnet run 进程");

        // 必须持续读走输出：重定向的管道写满后，`dotnet run` 与宿主都会被阻塞。
        var output = new StringBuilder();
        launcher.OutputDataReceived += (_, e) => AppendOutput(output, e.Data);
        launcher.ErrorDataReceived += (_, e) => AppendOutput(output, e.Data);
        launcher.BeginOutputReadLine();
        launcher.BeginErrorReadLine();

        lock (s_gate)
            s_launcherOutput[profileDirectory] = output;

        return launcher;
    }

    private static void AppendOutput(StringBuilder builder, string? line)
    {
        if (line is null)
            return;

        lock (builder)
        {
            // 只保留尾部：启动器可能长时间运行，输出无上限会吃内存。
            if (builder.Length > 8 * 1024)
                builder.Remove(0, builder.Length - 4 * 1024);

            builder.AppendLine(line);
        }
    }

    /// <summary>轮询发现文件，直到它就绪、启动器退出或超时。</summary>
    private static BrowserHostInfo? WaitForDiscovery(
        BrowserHostLocator locator, Process launcher, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var info = locator.Read();
            if (info is { } published && !string.IsNullOrWhiteSpace(published.Endpoint))
                return published;

            if (launcher.HasExited)
                return null;

            // E5：轮询间隔（实现细节，命中即返回）。
            Thread.Sleep(PollInterval);
        }

        return null;
    }

    private static void WaitUntilGone(int processId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!IsProcessAlive(processId))
                return;

            // E5：轮询间隔。
            Thread.Sleep(PollInterval);
        }
    }

    private static string ReadLauncherOutput(string profileDirectory)
    {
        lock (s_gate)
        {
            if (!s_launcherOutput.TryGetValue(profileDirectory, out var builder))
                return "（没有捕获到启动器输出）";

            lock (builder)
                return builder.ToString();
        }
    }

    private static void KillQuietly(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // 已经退出 / 权限不足：清理意图已达成或无法达成，都不该让测试失败。
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 同上
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录随后整体删除
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(CurrentFile())!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Seeing.Agent.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（Seeing.Agent.slnx）");
    }

    private static string CurrentFile(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
