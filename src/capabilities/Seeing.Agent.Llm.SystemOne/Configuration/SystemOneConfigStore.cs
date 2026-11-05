using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Llm.SystemOne.Configuration;

/// <summary>
/// SystemOne 配置文件存储：<c>~/.seeing/providers/systemone.json</c>。
/// <para>与其它插件 Provider 一致——配置放在 <c>providers/</c> 下、由 Provider 自己读写。
/// 不写进 <c>seeing.json</c> 的 <c>Providers</c> 节，是因为后者属于「用户手写的配置驱动 Provider」，
/// 而 SystemOne 是内置 Provider，装完即用。</para>
/// </summary>
public sealed class SystemOneConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly ILogger<SystemOneConfigStore> _logger;

    /// <summary>由工作区提供用户级配置目录。</summary>
    public SystemOneConfigStore(IWorkspaceProvider? workspace, ILogger<SystemOneConfigStore> logger)
        : this(
            workspace?.UserSeeingDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing"),
            logger)
    {
    }

    /// <summary>显式指定用户级 <c>.seeing</c> 目录（测试用）。</summary>
    public SystemOneConfigStore(string userSeeingDirectory, ILogger<SystemOneConfigStore> logger)
    {
        _directory = userSeeingDirectory ?? throw new ArgumentNullException(nameof(userSeeingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>配置文件路径。</summary>
    public string ConfigFilePath => Path.Combine(_directory, "providers", "systemone.json");

    /// <summary>读取配置；不存在或损坏时返回默认配置（不抛）。</summary>
    public async Task<SystemOneOptions> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
            return new SystemOneOptions();

        try
        {
            await using var stream = File.OpenRead(path);
            var options = await JsonSerializer.DeserializeAsync<SystemOneOptions>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return options ?? new SystemOneOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 SystemOne 配置失败，视为默认配置: {Path}", path);
            return new SystemOneOptions();
        }
    }

    /// <summary>写入配置（原子替换）。</summary>
    public async Task SaveAsync(SystemOneOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
        var path = ConfigFilePath;
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(options, JsonOptions);
        await File.WriteAllTextAsync(temp, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
