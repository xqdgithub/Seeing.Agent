using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.MiMo;

public sealed class MiMoConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ILogger<MiMoConfigStore> _logger;

    public MiMoConfigStore(IWorkspaceProvider? workspace, ILogger<MiMoConfigStore> logger)
        : this(
            workspace?.UserSeeingDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing"),
            logger)
    {
    }

    public MiMoConfigStore(string userSeeingDirectory, ILogger<MiMoConfigStore> logger)
    {
        _directory = userSeeingDirectory ?? throw new ArgumentNullException(nameof(userSeeingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ConfigFilePath => Path.Combine(_directory, "mimo.json");

    public async Task<MiMoOptions> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
            return new MiMoOptions();

        try
        {
            await using var stream = File.OpenRead(path);
            var options = await JsonSerializer.DeserializeAsync<MiMoOptions>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return options ?? new MiMoOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 MiMo 配置失败，视为空配置: {Path}", path);
            return new MiMoOptions();
        }
    }

    public async Task SaveAsync(MiMoOptions options, CancellationToken ct = default)
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
