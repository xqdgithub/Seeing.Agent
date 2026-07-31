using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Gateway.Channels;

/// <summary>
/// ChannelHost 进程管理器：启动/停止/重启 ChannelHost 子进程。
/// </summary>
public sealed class ChannelHostManager
{
    private readonly ChannelHostConfigStore _configStore;
    private readonly GatewayChannelRegistry _registry;
    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<ChannelHostManager> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public ChannelHostManager(
        ChannelHostConfigStore configStore,
        GatewayChannelRegistry registry,
        IWorkspaceProvider workspace,
        ILogger<ChannelHostManager> logger)
    {
        _configStore = configStore;
        _registry = registry;
        _workspace = workspace;
        _logger = logger;
    }

    public async Task StartAsync(string channelId, CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(channelId, ct).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task StartCoreAsync(string channelId, CancellationToken ct = default)
    {
        var hosts = _configStore.GetChannelHosts();
        var entry = hosts.FirstOrDefault(h => h.ChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未知 Channel: {channelId}");

        if (!entry.Enabled)
            throw new InvalidOperationException("Channel 未启用，请先启用后再启动。");

        var typeInfo = _registry.GetTypeInfo(channelId)
            ?? throw new InvalidOperationException($"未注册 Channel: {channelId}");

        var existing = await _configStore.LoadRuntimeStateAsync(channelId, ct);
        if (existing.ProcessId is int pid && IsProcessAlive(pid))
        {
            _logger.LogInformation("Channel {ChannelId} 已在运行 (PID {Pid})", channelId, pid);
            return;
        }

        var host = ResolveChannelHost()
            ?? throw new FileNotFoundException(
                "找不到可运行的 Seeing.Gateway.ChannelHost。请执行: dotnet build samples/Seeing.Gateway.ChannelHost");

        var pluginPath = ResolvePluginPath(typeInfo.AssemblyPath);
        var configPath = Path.GetFullPath(_configStore.GetRuntimeConfigPath(channelId));

        if (!File.Exists(configPath))
        {
            _logger.LogInformation("Channel 配置文件不存在，跳过启动: {ChannelId}，请先配置 Channel", channelId);
            return;
        }

        var state = new GatewayClientRuntimeState
        {
            Status = GatewayClientStatuses.Starting,
            StartedAt = DateTimeOffset.Now
        };
        await _configStore.SaveRuntimeStateAsync(channelId, state, ct);

        try
        {
            var startInfo = BuildChannelHostStartInfo(host.HostDll, host.WorkingDirectory, pluginPath, configPath);
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Channel 进程启动失败");

            await Task.Delay(800, ct);
            if (process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"Channel 进程启动后立即退出，代码 {process.ExitCode}"
                        : error.Trim());
            }

            state.ProcessId = process.Id;
            state.Status = GatewayClientStatuses.Running;
            state.LastError = null;
            await _configStore.SaveRuntimeStateAsync(channelId, state, ct);

            _ = Task.Run(() => PumpProcessOutputAsync(process, channelId), CancellationToken.None);

            _logger.LogInformation(
                "已启动 ChannelHost {ChannelId}, PID={Pid}, Host={HostDir}",
                channelId,
                process.Id,
                host.WorkingDirectory);
        }
        catch (Exception ex)
        {
            state.Status = GatewayClientStatuses.Error;
            state.ProcessId = null;
            state.LastError = ex.Message;
            await _configStore.SaveRuntimeStateAsync(channelId, state, ct);
            throw;
        }
    }

    public async Task StopAsync(string channelId, CancellationToken ct = default)
    {
        var state = await _configStore.LoadRuntimeStateAsync(channelId, ct);
        if (state.ProcessId is not int pid)
        {
            state.Status = GatewayClientStatuses.Stopped;
            await _configStore.SaveRuntimeStateAsync(channelId, state, ct);
            return;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止 ChannelHost {ChannelId} 时发生异常", channelId);
        }

        state.ProcessId = null;
        state.Status = GatewayClientStatuses.Stopped;
        await _configStore.SaveRuntimeStateAsync(channelId, state, ct);
    }

    public async Task RestartAsync(string channelId, CancellationToken ct = default)
    {
        await StopAsync(channelId, ct);
        await StartAsync(channelId, ct);
    }

    public async Task StartEnabledAsync(CancellationToken ct = default)
    {
        var hosts = _configStore.GetChannelHosts();
        foreach (var entry in hosts.Where(h => h.Enabled))
        {
            var state = await _configStore.LoadRuntimeStateAsync(entry.ChannelId, ct);
            if (state.ProcessId is int pid && IsProcessAlive(pid))
            {
                _logger.LogDebug("ChannelHost {ChannelId} 已在运行，跳过", entry.ChannelId);
                continue;
            }

            try
            {
                await StartAsync(entry.ChannelId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动启动 ChannelHost {ChannelId} 失败", entry.ChannelId);
            }
        }
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        var hosts = _configStore.GetChannelHosts();
        foreach (var entry in hosts)
        {
            var state = await _configStore.LoadRuntimeStateAsync(entry.ChannelId, ct);
            if (state.ProcessId is not int pid || !IsProcessAlive(pid))
                continue;

            try
            {
                await StopAsync(entry.ChannelId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止 ChannelHost {ChannelId} 时出现异常", entry.ChannelId);
            }
        }
    }

    public static async Task<bool> IsChannelConnectedAsync(
        string channelId,
        string gatewayBaseUrl,
        CancellationToken ct = default)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"{gatewayBaseUrl.TrimEnd('/')}/api/admin/channels/connected";
            var response = await httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("channels", out var channels))
                return false;

            foreach (var element in channels.EnumerateArray())
            {
                if (element.GetString()?.Equals(channelId, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task PumpProcessOutputAsync(Process process, string channelId)
    {
        try
        {
            var stdoutTask = PumpStreamAsync(
                process.StandardOutput,
                line => _logger.LogInformation("[{ChannelId}] {Line}", channelId, line));

            var stderrTask = PumpStreamAsync(
                process.StandardError,
                line => _logger.LogWarning("[{ChannelId}] {Line}", channelId, line));

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            var runtime = await _configStore.LoadRuntimeStateAsync(channelId, CancellationToken.None)
                .ConfigureAwait(false);
            if (runtime.ProcessId != process.Id)
                return;

            runtime.ProcessId = null;
            runtime.Status = process.ExitCode == 0
                ? GatewayClientStatuses.Stopped
                : GatewayClientStatuses.Error;
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(runtime.LastError))
                runtime.LastError = $"进程退出，代码 {process.ExitCode}";

            await _configStore.SaveRuntimeStateAsync(channelId, runtime, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 ChannelHost {ChannelId} 输出时出现异常", channelId);
        }
    }

    private static async Task PumpStreamAsync(
        StreamReader reader,
        Action<string> onLine)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
                break;

            if (!string.IsNullOrWhiteSpace(line))
                onLine(line);
        }
    }

    public static bool IsProcessAlive(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePluginPath(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Channel 插件不存在: {fullPath}");

        return fullPath;
    }

    private ChannelHostLocation? ResolveChannelHost()
    {
        const string tfm = "net10.0";
        const string dllName = "Seeing.Gateway.ChannelHost.dll";

        _logger.LogDebug("ResolveChannelHost: BaseDirectory={BaseDirectory}", AppContext.BaseDirectory);

        var rootDll = Path.Combine(AppContext.BaseDirectory, dllName);
        _logger.LogDebug("ResolveChannelHost: Checking root {Path}", rootDll);
        if (IsRunnableChannelHost(rootDll, AppContext.BaseDirectory))
        {
            _logger.LogDebug("ResolveChannelHost: Found {Path}", rootDll);
            return new ChannelHostLocation(rootDll, AppContext.BaseDirectory);
        }

        foreach (var dir in GetChannelHostProjectOutputDirs(tfm))
        {
            var dll = Path.Combine(dir, dllName);
            if (IsRunnableChannelHost(dll, dir))
                return new ChannelHostLocation(dll, dir);
        }

        return null;
    }

    private static bool IsRunnableChannelHost(string dllPath, string workingDirectory)
    {
        if (!File.Exists(dllPath))
            return false;

        var depsJSON = Path.Combine(workingDirectory, "Seeing.Gateway.ChannelHost.deps.json");
        return File.Exists(depsJSON);
    }

    private IEnumerable<string> GetChannelHostProjectOutputDirs(string tfm)
    {
        foreach (var root in GetSearchRoots())
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                yield return Path.GetFullPath(Path.Combine(root, "samples", "Seeing.Gateway.ChannelHost", "bin", configuration, tfm));
                yield return Path.GetFullPath(Path.Combine(root, "..", "Seeing.Gateway.ChannelHost", "bin", configuration, tfm));
            }
        }
    }

    private IEnumerable<string> GetSearchRoots()
    {
        yield return _workspace.WorkspaceRoot;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }
    }

    private sealed record ChannelHostLocation(string HostDll, string WorkingDirectory);

    private static ProcessStartInfo BuildChannelHostStartInfo(
        string hostPath, string workingDirectory, string pluginPath, string configPath)
    {
        return new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{hostPath}\" --plugin \"{pluginPath}\" --config \"{configPath}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
    }
}
