using System.Diagnostics;
using Seeing.Agent.Configuration;
using Seeing.Agent.Gateway.Channels;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// Gateway Client 独立进程监督器（启动/停止/状态）
/// </summary>
public sealed class GatewayClientSupervisor
{
    private readonly GatewayClientConfigService _configService;
    private readonly GatewayChannelRegistry _registry;
    private readonly ILogger<GatewayClientSupervisor> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public GatewayClientSupervisor(
        GatewayClientConfigService configService,
        GatewayChannelRegistry registry,
        ILogger<GatewayClientSupervisor> logger)
    {
        _configService = configService;
        _registry = registry;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GatewayClientViewModel>> RefreshStatusesAsync(CancellationToken ct = default)
    {
        var clients = (await _configService.GetClientsAsync(ct)).ToList();
        foreach (var client in clients)
        {
            await RefreshStatusAsync(client, ct);
        }

        return clients;
    }

    public async Task RefreshStatusAsync(GatewayClientViewModel client, CancellationToken ct = default)
    {
        var state = await _configService.LoadRuntimeStateAsync(client.ChannelId, ct);

        if (!client.Enabled)
        {
            client.Status = GatewayClientStatuses.Disabled;
            client.ProcessId = null;
            client.LastError = state.LastError;
            return;
        }

        if (state.ProcessId is int pid && IsProcessAlive(pid))
        {
            client.Status = GatewayClientStatuses.Running;
            client.ProcessId = pid;
            client.LastError = null;
            return;
        }

        client.Status = string.IsNullOrWhiteSpace(state.LastError)
            ? GatewayClientStatuses.Stopped
            : GatewayClientStatuses.Error;
        client.ProcessId = null;
        client.LastError = state.LastError;
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
        var clients = await _configService.GetClientsAsync(ct);
        var client = clients.FirstOrDefault(c => c.ChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未知 Channel: {channelId}");

        if (!client.Enabled)
            throw new InvalidOperationException("Channel 未启用，请打开开关后再启动。");

        var typeInfo = _registry.GetTypeInfo(channelId)
            ?? throw new InvalidOperationException($"未注册 Channel: {channelId}");

        var existing = await _configService.LoadRuntimeStateAsync(channelId, ct);
        if (existing.ProcessId is int pid && IsProcessAlive(pid))
        {
            _logger.LogInformation("Channel {ChannelId} 已在运行 (PID {Pid})", channelId, pid);
            return;
        }

        var host = ResolveChannelHost()
            ?? throw new FileNotFoundException(
                "找不到可运行的 Seeing.Gateway.ChannelHost。请执行: dotnet build samples/Seeing.Agent.WebUI");

        var pluginPath = ResolvePluginPath(typeInfo.AssemblyPath);
        var configPath = Path.GetFullPath(_configService.GetRuntimeConfigPath(channelId));
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Channel 配置文件不存在: {configPath}");

        var state = new GatewayClientRuntimeState
        {
            Status = GatewayClientStatuses.Starting,
            StartedAt = DateTimeOffset.Now
        };
        await _configService.SaveRuntimeStateAsync(channelId, state, ct);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{host.HostDll}\" --plugin \"{pluginPath}\" --config \"{configPath}\"",
                WorkingDirectory = host.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

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
            await _configService.SaveRuntimeStateAsync(channelId, state, ct);

            _ = Task.Run(() => PumpProcessOutputAsync(process, channelId), CancellationToken.None);

            _logger.LogInformation(
                "已启动 Gateway Client {ChannelId}, PID={Pid}, Host={HostDir}",
                channelId,
                process.Id,
                host.WorkingDirectory);
        }
        catch (Exception ex)
        {
            state.Status = GatewayClientStatuses.Error;
            state.ProcessId = null;
            state.LastError = ex.Message;
            await _configService.SaveRuntimeStateAsync(channelId, state, ct);
            throw;
        }
    }

    public async Task StopAsync(string channelId, CancellationToken ct = default)
    {
        var state = await _configService.LoadRuntimeStateAsync(channelId, ct);
        if (state.ProcessId is not int pid)
        {
            state.Status = GatewayClientStatuses.Stopped;
            await _configService.SaveRuntimeStateAsync(channelId, state, ct);
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
            _logger.LogWarning(ex, "停止 Channel {ChannelId} 时发生异常", channelId);
        }

        state.ProcessId = null;
        state.Status = GatewayClientStatuses.Stopped;
        await _configService.SaveRuntimeStateAsync(channelId, state, ct);
    }

    public async Task RestartAsync(string channelId, CancellationToken ct = default)
    {
        await StopAsync(channelId, ct);
        await StartAsync(channelId, ct);
    }

    public async Task StartEnabledClientsAsync(CancellationToken ct = default)
    {
        var clients = await _configService.GetClientsAsync(ct);
        foreach (var client in clients.Where(c => c.Enabled))
        {
            await RefreshStatusAsync(client, ct);
            if (client.Status == GatewayClientStatuses.Running)
                continue;

            try
            {
                await StartAsync(client.ChannelId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动启动 Gateway Client {ChannelId} 失败", client.ChannelId);
            }
        }
    }

    public async Task StopRunningClientsAsync(CancellationToken ct = default)
    {
        var clients = await _configService.GetClientsAsync(ct);
        foreach (var client in clients)
        {
            await RefreshStatusAsync(client, ct);
            if (client.Status != GatewayClientStatuses.Running)
                continue;

            try
            {
                await StopAsync(client.ChannelId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止 Gateway Client {ChannelId} 时出现异常", client.ChannelId);
            }
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

            var runtime = await _configService.LoadRuntimeStateAsync(channelId, CancellationToken.None)
                .ConfigureAwait(false);
            if (runtime.ProcessId != process.Id)
                return;

            runtime.ProcessId = null;
            runtime.Status = process.ExitCode == 0
                ? GatewayClientStatuses.Stopped
                : GatewayClientStatuses.Error;
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(runtime.LastError))
                runtime.LastError = $"进程退出，代码 {process.ExitCode}";

            await _configService.SaveRuntimeStateAsync(channelId, runtime, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 Channel {ChannelId} 输出时出现异常", channelId);
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

    private static bool IsProcessAlive(int pid)
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

    private static ChannelHostLocation? ResolveChannelHost()
    {
        const string tfm = "net10.0";
        const string dllName = "Seeing.Gateway.ChannelHost.dll";

        var bundledDir = Path.Combine(AppContext.BaseDirectory, "channel-host");
        var bundledDll = Path.Combine(bundledDir, dllName);
        if (IsRunnableChannelHost(bundledDll, bundledDir))
            return new ChannelHostLocation(bundledDll, bundledDir);

        foreach (var dir in GetChannelHostProjectOutputDirs(tfm))
        {
            var dll = Path.Combine(dir, dllName);
            if (IsRunnableChannelHost(dll, dir))
                return new ChannelHostLocation(dll, dir);
        }

        return null;
    }

    private static bool IsRunnableChannelHost(string dllPath, string workingDirectory) =>
        File.Exists(dllPath)
        && File.Exists(Path.Combine(workingDirectory, "Microsoft.Extensions.Hosting.Abstractions.dll"));

    private static IEnumerable<string> GetChannelHostProjectOutputDirs(string tfm)
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

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return Directory.GetCurrentDirectory();

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }
    }

    private sealed record ChannelHostLocation(string HostDll, string WorkingDirectory);
}
