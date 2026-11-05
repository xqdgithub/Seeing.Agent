using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using System.Text;
using System.Text.Json;

namespace Seeing.Provider.TypeSafeApi;

/// <summary>
/// TypeSafe 端点配置文件存储：<c>~/.seeing/providers/typesafe-api.json</c>。
/// <para>与其它插件 Provider 一致——配置放在 <c>providers/</c> 下、由 Provider 自己读写。</para>
/// <para><b>旧文件回退读</b>：早先一份内建 Provider 把配置写在
/// <c>providers/systemone.json</c>。该文件可能已含用户填好的 apiKey，
/// 直接丢弃等于静默清空凭据，故新文件不存在时只读回退到旧文件；
/// 保存一律写新文件，用户下次保存即自然完成迁移。</para>
/// </summary>
public sealed class TypeSafeApiConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>已废弃的旧配置文件名（只读回退，不再写入）。</summary>
    public const string LegacyFileName = "systemone.json";

    private readonly string _directory;
    private readonly ILogger<TypeSafeApiConfigStore> _logger;

    /// <summary>由工作区提供用户级配置目录。</summary>
    public TypeSafeApiConfigStore(IWorkspaceProvider? workspace, ILogger<TypeSafeApiConfigStore> logger)
        : this(
            workspace?.UserSeeingDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing"),
            logger)
    {
    }

    /// <summary>显式指定用户级 <c>.seeing</c> 目录（测试用）。</summary>
    public TypeSafeApiConfigStore(string userSeeingDirectory, ILogger<TypeSafeApiConfigStore> logger)
    {
        _directory = userSeeingDirectory ?? throw new ArgumentNullException(nameof(userSeeingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>配置文件路径。</summary>
    public string ConfigFilePath => Path.Combine(_directory, "providers", "typesafe-api.json");

    /// <summary>旧配置文件路径（迁移回退用）。</summary>
    public string LegacyConfigFilePath => Path.Combine(_directory, "providers", LegacyFileName);

    /// <summary>读取配置；不存在或损坏时返回默认配置（不抛）。</summary>
    public async Task<TypeSafeApiOptions> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigFilePath;
        if (File.Exists(path))
            return await ReadAsync(path, ct).ConfigureAwait(false);

        var legacy = LegacyConfigFilePath;
        if (!File.Exists(legacy))
            return new TypeSafeApiOptions();

        _logger.LogInformation(
            "检测到旧 SystemOne 配置，按回退读加载；下次保存将写入新路径。旧={Legacy} 新={Path}",
            legacy,
            path);

        return await ReadAsync(legacy, ct).ConfigureAwait(false);
    }

    /// <summary>写入配置（原子替换）。</summary>
    public async Task SaveAsync(TypeSafeApiOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
        var path = ConfigFilePath;
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(options, JsonOptions);
        await File.WriteAllTextAsync(temp, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private async Task<TypeSafeApiOptions> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var options = await JsonSerializer.DeserializeAsync<TypeSafeApiOptions>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return options ?? new TypeSafeApiOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 TypeSafe Provider 配置失败，视为默认配置: {Path}", path);
            return new TypeSafeApiOptions();
        }
    }
}
