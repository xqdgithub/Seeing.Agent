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

        var logDir = Path.Combine(_workspaceRoot, ".seeing", "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"{service}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var contentRoot = Path.GetDirectoryName(dllPath)!;
        var psi = new ProcessStartInfo("dotnet", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workspaceRoot
        };
        psi.Environment["ASPNETCORE_CONTENTROOT"] = contentRoot;
        psi.Environment["SEEING_WORKSPACE_ROOT"] = _workspaceRoot;

        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException($"无法启动 {service} 进程");

        // 逐行异步写入日志文件，AutoFlush 确保实时落盘
        _ = Task.Run(async () =>
        {
            using var writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
            var stdoutTask = PipeToWriterAsync(process.StandardOutput, writer);
            var stderrTask = PipeToWriterAsync(process.StandardError, writer);
            await Task.WhenAll(stdoutTask, stderrTask);
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

    private static async Task PipeToWriterAsync(StreamReader reader, StreamWriter writer)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }
}