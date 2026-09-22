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
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"找不到 {service} 的 dll 文件: {dllPath}");
        ct.ThrowIfCancellationRequested();

        var logDir = Path.Combine(_workspaceRoot, ".seeing", "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"{service}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var contentRoot = Path.GetDirectoryName(dllPath)!;
        var psi = CreateStartInfo(dllPath, _workspaceRoot, extraArgs, environment);
        psi.Environment["ASPNETCORE_CONTENTROOT"] = contentRoot;
        psi.Environment["SEEING_WORKSPACE_ROOT"] = _workspaceRoot;

        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"无法启动 {service} 进程");

        // 逐行异步写入日志文件，AutoFlush 确保实时落盘
        _ = Task.Run(async () =>
        {
            try
            {
                using var writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
                var stdoutTask = PipeToWriterAsync(process.StandardOutput, writer, "stdout");
                var stderrTask = PipeToWriterAsync(process.StandardError, writer, "stderr");
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (Exception ex)
            {
                try
                {
                    await File.AppendAllTextAsync(
                        logFile,
                        $"[{DateTimeOffset.Now:O}] [cli-log-pump] {ex.Message}{Environment.NewLine}");
                }
                catch
                {
                    // 日志管道失败不能影响 CLI 主流程。
                }
            }
        });

        var record = new InstanceRecord
        {
            Id = $"{service}-{Guid.NewGuid().ToString("N")[..6]}",
            Service = service,
            Pid = process.Id,
            WorkspaceRoot = _workspaceRoot,
            Port = port,
            StartedAt = DateTime.UtcNow,
            LogPath = logFile
        };

        Registry.Add(record);
        return record;
    }

    /// <summary>
    /// 前台启动：子进程继承当前终端，不重定向输出（故不写日志文件），
    /// 仍登记实例以便 status 可见、stop（HTTP shutdown）可用。
    /// 调用方负责在进程退出后 <see cref="InstanceRegistry.Remove"/> 并释放进程对象。
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

        var psi = ForegroundProcessRunner.CreateStartInfo(
            dllPath,
            _workspaceRoot,
            extraArgs,
            environment);
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
            // 前台模式输出直接进终端，不落日志文件；留空以避免提示指向不存在的文件。
            LogPath = string.Empty
        };

        Registry.Add(record);
        return (process, record);
    }

    /// <summary>
    /// 构造子进程启动信息，使用 ArgumentList 避免端口参数因字符串拼接而被错误解析。
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(
        string dllPath,
        string workingDirectory,
        IEnumerable<string>? extraArgs = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        psi.ArgumentList.Add(dllPath);
        if (extraArgs is not null)
        {
            foreach (var arg in extraArgs)
                psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var entry in environment)
                psi.Environment[entry.Key] = entry.Value;
        }

        return psi;
    }

    public bool IsProcessRunning(InstanceRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.Pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StopAsync(
        InstanceRecord record,
        ManagementApiClient apiClient,
        CancellationToken ct = default)
    {
        var shutdownOk = false;
        // 子进程已因端口冲突退出时，绝不能向占用该端口的其他程序发送 shutdown 请求。
        if (IsProcessRunning(record))
        {
            try
            {
                shutdownOk = await apiClient.ShutdownAsync(ct);
            }
            catch
            {
                // API 不可用时继续使用进程终止兜底。
            }
        }

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
        CancellationToken ct = default,
        InstanceRecord? process = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // 先确认是本次启动的子进程仍在运行，避免把其他程序对同一端口的响应
            // 误判为 WebUI 已就绪。
            if (process is not null && !IsProcessRunning(process))
            {
                var details = ReadLogTail(process.LogPath);
                var suffix = string.IsNullOrWhiteSpace(details)
                    ? string.Empty
                    : $"{Environment.NewLine}最近日志:{Environment.NewLine}{details}";
                throw new InvalidOperationException(
                    $"{DescribeOutput(process)}进程已退出。{suffix}");
            }

            var ready = checkGatewayHealth
                ? await apiClient.HealthCheckAsync(ct)
                : await apiClient.ReachableAsync(ct);
            if (ready)
                return;

            await Task.Delay(500, ct);
        }

        var logSuffix = process is null || string.IsNullOrEmpty(process.LogPath)
            ? string.Empty
            : $"；日志: {process.LogPath}";
        throw new TimeoutException($"服务在 {timeoutSeconds} 秒内未就绪{logSuffix}");
    }

    /// <summary>前台实例不写日志文件，其输出即当前终端；据此给出可用的提示文案。</summary>
    internal static string DescribeOutput(InstanceRecord record)
        => string.IsNullOrEmpty(record.LogPath)
            ? "（前台模式，输出见当前终端）"
            : $"详见日志: {record.LogPath}";

    private static async Task PipeToWriterAsync(
        StreamReader reader,
        StreamWriter writer,
        string streamName)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            await writer.WriteLineAsync(
                $"[{DateTimeOffset.Now:O}] [{streamName}] {line}").ConfigureAwait(false);
        }
    }

    internal static string ReadLogTail(string logPath, int maxLines = 12)
    {
        if (maxLines <= 0 || !File.Exists(logPath)) return string.Empty;

        try
        {
            var lines = File.ReadLines(logPath).TakeLast(maxLines);
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }
}