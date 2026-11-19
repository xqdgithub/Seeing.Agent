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

        var useNoBuild = CliIsBuilt();
        var launcher = StartLauncher(
            locator.ProfileDirectory,
            ["--browser-host", "--profile", locator.ProfileDirectory, "--idle-seconds", idleSeconds.ToString(CultureInfo.InvariantCulture)],
            useNoBuild);

        var info = WaitForDiscovery(locator, launcher, useNoBuild ? NoBuildTimeout : BuildTimeout);
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
    /// 一次性运行宿主并等它自行退出（用于验证退出码与发现文件终态）。
    /// </summary>
    /// <param name="profileDirectory">专用 profile 目录（同时作为发现文件根目录）。</param>
    /// <param name="hostArgs">
    /// 传给宿主、且不含 <c>--browser-host</c> / <c>--profile</c> 的额外参数
    /// （这两个由本方法补齐；<c>--idle-seconds</c> 默认 60）。
    /// </param>
    /// <param name="timeout">等待退出的上限。</param>
    /// <returns>退出码、退出后的发现文件内容（未写/已删为 <see langword="null"/>）与启动器输出。</returns>
    public static HostRunResult RunToExit(
        string profileDirectory,
        IReadOnlyList<string> hostArgs,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        Directory.CreateDirectory(profileDirectory);

        var locator = new BrowserHostLocator(profileDirectory, profileDirectory);
        var hostArguments = new List<string> { "--browser-host", "--profile", locator.ProfileDirectory };

        if (!hostArgs.Contains("--idle-seconds", StringComparer.Ordinal))
        {
            hostArguments.Add("--idle-seconds");
            hostArguments.Add("60");
        }

        hostArguments.AddRange(hostArgs);

        var launcher = StartLauncher(locator.ProfileDirectory, hostArguments, CliIsBuilt());
        try
        {
            // WaitForExit 是事件驱动的上限等待（不是固定 sleep）：命中即返回。
            if (!launcher.WaitForExit((int)timeout.TotalMilliseconds))
            {
                KillQuietly(launcher);
                return new HostRunResult(
                    -1,
                    locator.Read(),
                    "（宿主在超时内没有退出）" + Environment.NewLine + ReadLauncherOutput(locator.ProfileDirectory));
            }

            var exitCode = launcher.ExitCode;
            return new HostRunResult(exitCode, locator.Read(), ReadLauncherOutput(locator.ProfileDirectory));
        }
        finally
        {
            launcher.Dispose();
        }
    }

    /// <summary>
    /// 等宿主启动器（<c>dotnet run</c>）退出并返回它的退出码。
    /// </summary>
    /// <param name="profileDirectory">启动时使用的 profile 目录。</param>
    /// <param name="timeout">等待上限。</param>
    /// <returns>启动器退出码；超时返回 -1，找不到启动器返回 <see cref="int.MinValue"/>。</returns>
    /// <remarks>
    /// <b>为什么不能直接用宿主 <see cref="Process"/>.ExitCode</b>：那个句柄来自
    /// <c>Process.GetProcessById</c>，不是本进程 Start 出来的，.NET 会拒绝提供退出码。
    /// <c>dotnet run</c> 会把子进程的退出码原样作为自己的退出码，因此这里读启动器即可。
    /// </remarks>
    public static int WaitForLauncherExit(string profileDirectory, TimeSpan timeout)
    {
        var profile = BrowserHostLocator.NormalizeProfileDirectory(profileDirectory);

        Process? launcher;
        lock (s_gate)
            s_launchers.TryGetValue(profile, out launcher);

        if (launcher is null)
            return int.MinValue;

        return launcher.WaitForExit((int)timeout.TotalMilliseconds) ? launcher.ExitCode : -1;
    }

    /// <summary>一次性宿主运行的结果。</summary>
    /// <param name="ExitCode">进程退出码（超时未退出为 -1）。</param>
    /// <param name="Discovery">退出后读到的发现文件；不存在为 <see langword="null"/>。</param>
    /// <param name="Output">启动器输出（诊断用）。</param>
    public sealed record HostRunResult(int ExitCode, BrowserHostInfo? Discovery, string Output);

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

    /// <summary>启动 <c>dotnet run --project samples/Seeing.Agent.Cli -- &lt;hostArgs&gt;</c>。</summary>
    private static Process StartLauncher(
        string profileDirectory, IReadOnlyList<string> hostArgs, bool useNoBuild)
    {
        var projectPath = Path.Combine(FindRepositoryRoot(), "samples", "Seeing.Agent.Cli", "Seeing.Agent.Cli.csproj");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // 测试隔离：发现文件 / 日志 / 客户端锁都落在本次 profile 里
            // （BrowserHostLocator.ResolveBaseDirectory 读同一个变量，单点定义）。
            Environment = { [BrowserHostLocator.BaseDirectoryVariable] = profileDirectory },
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        if (useNoBuild)
            startInfo.ArgumentList.Add("--no-build");

        startInfo.ArgumentList.Add("--");
        foreach (var arg in hostArgs)
            startInfo.ArgumentList.Add(arg);

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

    /// <summary>
    /// CLI 产物是否已经构建（决定 <c>dotnet run</c> 是否带 <c>--no-build</c>）。
    /// </summary>
    /// <remarks>
    /// 用「文件是否存在」而不是「先跑一次再按输出猜」：后者要靠错误文案判断，
    /// 文案一变就退化成每次都全量构建。配置目录（Debug/Release）从测试程序集路径反推，
    /// 保证 <c>dotnet run</c> 找的就是测试实际使用的那份产物。
    /// </remarks>
    private static bool CliIsBuilt()
    {
        var dll = Path.Combine(
            FindRepositoryRoot(), "samples", "Seeing.Agent.Cli", "bin",
            TestConfiguration(), "net10.0", "seeing-cli.dll");

        return File.Exists(dll);
    }

    /// <summary>从测试程序集路径反推构建配置（<c>.../bin/Debug/net10.0/</c>）。</summary>
    private static string TestConfiguration()
    {
        var parts = AppContext.BaseDirectory.Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var binIndex = Array.LastIndexOf(parts, "bin");
        return binIndex >= 0 && binIndex + 1 < parts.Length ? parts[binIndex + 1] : "Debug";
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
