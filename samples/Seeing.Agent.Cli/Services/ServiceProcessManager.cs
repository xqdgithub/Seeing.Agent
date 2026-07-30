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
