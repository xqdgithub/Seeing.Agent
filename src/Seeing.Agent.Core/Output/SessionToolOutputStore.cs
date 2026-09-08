using Microsoft.Extensions.Logging;
using Seeing.Session.Storage;

namespace Seeing.Agent.Output;

/// <summary>
/// 基于会话存储目录的工具输出落盘实现。
/// <para>ref 目录解析跟随 IRelocatableSessionStore.BaseDirectory（工作区切换自动重定位），
/// 无法解析时回退到 FileSessionStore 默认目录（~/.seeing/sessions）。</para>
/// </summary>
public sealed class SessionToolOutputStore : IToolOutputStore
{
    private const string RefDirectorySuffix = ".ref";
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(5);

    private readonly ISessionStore _store;
    private readonly ILogger<SessionToolOutputStore>? _logger;

    public SessionToolOutputStore(ISessionStore store, ILogger<SessionToolOutputStore>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    /// <inheritdoc />
    public string GetRefDirectory(string sessionId)
    {
        var sessionDir = (_store as IRelocatableSessionStore)?.BaseDirectory
                         ?? FileSessionStore.GetDefaultSessionDirectory();
        return Path.Combine(sessionDir, SanitizeName(sessionId) + RefDirectorySuffix);
    }

    /// <inheritdoc />
    public async Task<string> SaveAsync(string sessionId, string callId, string content, CancellationToken cancellationToken = default)
    {
        var refDir = GetRefDirectory(sessionId);
        Directory.CreateDirectory(refDir);

        var fileName = SanitizeName(callId) + ".txt";
        var filePath = Path.Combine(refDir, fileName);

        using var timeoutCts = new CancellationTokenSource(SaveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await File.WriteAllTextAsync(filePath, content, System.Text.Encoding.UTF8, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning("[SessionToolOutputStore] 落盘超时，放弃写入: {FilePath}", filePath);
            throw new TimeoutException($"工具输出落盘超时: {filePath}");
        }

        _logger?.LogDebug("工具输出已落盘: {FilePath} ({Chars} 字符)", filePath, content.Length);
        return filePath;
    }

    /// <inheritdoc />
    public void DeleteSessionRefDirectory(string sessionId)
    {
        try
        {
            var refDir = GetRefDirectory(sessionId);
            if (Directory.Exists(refDir))
            {
                Directory.Delete(refDir, recursive: true);
                _logger?.LogDebug("已删除会话引用目录: {RefDir}", refDir);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "删除会话引用目录失败: {SessionId}", sessionId);
        }
    }

    /// <summary>净化文件名非法字符，空结果回退 GUID。</summary>
    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Guid.NewGuid().ToString("N");

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }
}
