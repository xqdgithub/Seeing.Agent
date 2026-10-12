using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;

namespace Seeing.Provider.SiliconFlow;

public sealed class SiliconFlowConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ILogger<SiliconFlowConfigStore> _logger;

    public SiliconFlowConfigStore(IWorkspaceProvider? workspace, ILogger<SiliconFlowConfigStore> logger)
        : this(
            workspace?.UserSeeingDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing"),
            logger)
    {
    }

    public SiliconFlowConfigStore(string userSeeingDirectory, ILogger<SiliconFlowConfigStore> logger)
    {
        _directory = userSeeingDirectory ?? throw new ArgumentNullException(nameof(userSeeingDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ConfigFilePath => Path.Combine(_directory, "providers", "siliconflow.json");

    public async Task<SiliconFlowOptions> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigFilePath;
        if (!File.Exists(path))
            return new SiliconFlowOptions();

        try
        {
            await using var stream = File.OpenRead(path);
            var options = await JsonSerializer.DeserializeAsync<SiliconFlowOptions>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
            return options ?? new SiliconFlowOptions();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 SiliconFlow 配置失败，视为空配置: {Path}", path);
            return new SiliconFlowOptions();
        }
    }

    public async Task SaveAsync(SiliconFlowOptions options, CancellationToken ct = default)
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
