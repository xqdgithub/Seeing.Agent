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

    public async Task<ReadTextFileResponse> ReadTextFileAsync(
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
            return new ReadTextFileResponse { Content = "" };
        }

        if (!File.Exists(fullPath))
            return new ReadTextFileResponse { Content = "" };

        try
        {
            // 流式限长：最多读取 maxChars 个字符，避免大文件整文件入内存
            var maxChars = Math.Max(0, Math.Min(limit ?? DefaultReadLimit * MaxLineLength, MaxBytes));

            string content;
            await using (var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true))
            using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
            {
                content = line.HasValue && line.Value > 0
                    ? await ReadLineAsync(reader, line.Value, cancellationToken).ConfigureAwait(false) ?? ""
                    : await ReadLimitedAsync(reader, maxChars, cancellationToken).ConfigureAwait(false);
            }

            if (content.Length > maxChars)
                content = content[..maxChars];

            return new ReadTextFileResponse { Content = content };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACP read failed for {Path}", fullPath);
            return new ReadTextFileResponse { Content = "" };
        }
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken)
    {
        if (maxChars <= 0)
            return string.Empty;

        var buffer = new char[maxChars];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await reader
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
        }

        return new string(buffer, 0, total);
    }

    private static async Task<string?> ReadLineAsync(
        StreamReader reader,
        int lineNumber,
        CancellationToken cancellationToken)
    {
        string? current = null;
        for (var i = 1; i <= lineNumber; i++)
        {
            current = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (current == null)
                return null;
        }

        return current;
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

    internal static bool TryResolvePath(string path, string workingDirectory, out string fullPath)
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

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // 分隔符边界：必须等于 root 本身，或以 root + 分隔符为前缀，
        // 避免 E:\ws\apple 命中 root=E:\ws\app 的前缀逃逸
        if (fullPath.Equals(root, comparison))
            return true;

        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootPrefix, comparison)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }
}
