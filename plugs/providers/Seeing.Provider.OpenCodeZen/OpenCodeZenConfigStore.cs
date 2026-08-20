using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.OpenCodeZen;

/// <summary>
/// OpenCode Zen 配置存储：持久化到 ~/.seeing/opencode-zen.json。
/// </summary>
public sealed class OpenCodeZenConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ILogger<OpenCodeZenConfigStore> _logger;

    public OpenCodeZenConfigStore(IWorkspaceProvider? workspace, ILogger<OpenCodeZenConfigStore> logger)
        : this(
            workspace?.UserSeeingDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing"),
            logger)
    {
    }

    public OpenCodeZenConfigStore(string userSeeingDirectory, ILogger<OpenCodeZenConfigStore> logger)
    {
        _directory = userSeeingDirectory ?? throw new ArgumentNullException(nameof(userSeeingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ConfigFilePath => Path.Combine(_directory, "opencode-zen.json");

    public async Task<OpenCodeZenOptions> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
            return new OpenCodeZenOptions();

        try
        {
            await using var stream = File.OpenRead(path);
            var options = await JsonSerializer.DeserializeAsync<OpenCodeZenOptions>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return options ?? new OpenCodeZenOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 OpenCode Zen 配置失败，视为空配置: {Path}", path);
            return new OpenCodeZenOptions();
        }
    }

    public async Task SaveAsync(OpenCodeZenOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(_directory);
        var path = ConfigFilePath;
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(options, JsonOptions);
        await File.WriteAllTextAsync(temp, json, Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
