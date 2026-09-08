using Acp.Messages;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Acp.Filesystem;

/// <summary>
/// ACP 文件系统回调桥接（路径白名单 + 输出截断，对齐 Seeing FileSystem 限制）。
/// </summary>
public sealed class AcpFileSystemBridge
{
    private const int DefaultReadLimit = 2000;
    private const int MaxLineLength = 2000;
    private const int MaxBytes = 50 * 1024;

    private readonly ILogger<AcpFileSystemBridge> _logger;

    public AcpFileSystemBridge(ILogger<AcpFileSystemBridge> logger)
    {
        _logger = logger;
    }

    public Task<ReadTextFileResponse> ReadTextFileAsync(
        string path,
        string sessionId,
        string workingDirectory,
        int? limit = null,
        int? line = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolvePath(path, workingDirectory, out var fullPath))
        {
            _logger.LogWarning("ACP read denied (path outside workspace): {Path}", path);
            return Task.FromResult(new ReadTextFileResponse { Content = "" });
        }

        if (!File.Exists(fullPath))
            return Task.FromResult(new ReadTextFileResponse { Content = "" });

        try
        {
            var content = File.ReadAllText(fullPath);

            if (line.HasValue && line.Value > 0)
            {
                var lines = content.Split('\n');
                content = line.Value <= lines.Length ? lines[line.Value - 1] : "";
            }

            var maxChars = limit ?? DefaultReadLimit * MaxLineLength;
            if (content.Length > maxChars)
                content = content[..maxChars];

            if (content.Length > MaxBytes)
                content = content[..MaxBytes];

            return Task.FromResult(new ReadTextFileResponse { Content = content });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACP read failed for {Path}", fullPath);
            return Task.FromResult(new ReadTextFileResponse { Content = "" });
        }
    }

    public Task<WriteTextFileResponse?> WriteTextFileAsync(
        string content,
        string path,
        string sessionId,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolvePath(path, workingDirectory, out var fullPath))
        {
            _logger.LogWarning("ACP write denied (path outside workspace): {Path}", path);
            return Task.FromResult<WriteTextFileResponse?>(new WriteTextFileResponse { Applied = false });
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(fullPath, content);
            return Task.FromResult<WriteTextFileResponse?>(new WriteTextFileResponse { Applied = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACP write failed for {Path}", fullPath);
            return Task.FromResult<WriteTextFileResponse?>(new WriteTextFileResponse { Applied = false });
        }
    }

    private static bool TryResolvePath(string path, string workingDirectory, out string fullPath)
    {
        fullPath = "";

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory);

        fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));

        return fullPath.StartsWith(root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }
}
