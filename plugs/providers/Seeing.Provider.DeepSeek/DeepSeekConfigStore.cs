using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.DeepSeek;

public sealed class DeepSeekConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ILogger<DeepSeekConfigStore> _logger;

    public DeepSeekConfigStore(IWorkspaceProvider? workspace, ILogger<DeepSeekConfigStore> logger)
        : this(
            workspace?.UserSeeingDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing"),
            logger)
    {
    }

    public DeepSeekConfigStore(string userSeeingDirectory, ILogger<DeepSeekConfigStore> logger)
    {
        _directory = userSeeingDirectory ?? throw new ArgumentNullException(nameof(userSeeingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ConfigFilePath => Path.Combine(_directory, "deepseek.json");

    public async Task<DeepSeekOptions> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
            return new DeepSeekOptions();

        try
        {
            await using var stream = File.OpenRead(path);
            var options = await JsonSerializer.DeserializeAsync<DeepSeekOptions>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return options ?? new DeepSeekOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 DeepSeek 配置失败，视为空配置: {Path}", path);
            return new DeepSeekOptions();
        }
    }

    public async Task SaveAsync(DeepSeekOptions options, CancellationToken ct = default)
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
