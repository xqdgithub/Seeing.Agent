using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Seeing.Session.Storage;

/// <summary>
/// 原子文件写入的共享实现：进程级按路径锁 + 唯一临时文件名 + 移动重试（复制兜底）。
/// <para>
/// 为什么需要它：<c>File.Move(overwrite: true)</c> 在 Windows 上会因 Defender 扫描、
/// 文件系统缓存未刷新、其它句柄短暂占用而抛出 <see cref="UnauthorizedAccessException"/>
/// 或 <see cref="IOException"/>。这类失败是瞬时的，重试即可；重试耗尽后改用
/// 「先复制覆盖、再删除源」的兜底，保证目标文件不会被提前毁掉。
/// </para>
/// <para>
/// 锁必须跨实例共享：同一个目录可能被多个 <see cref="FileSessionStore"/> /
/// <see cref="FileSessionGroupStore"/> 实例（冷/热实例、宿主与 CLI）同时访问，
/// 实例级锁无法互斥，会导致固定临时文件名互相覆盖。
/// </para>
/// </summary>
internal static class AtomicFileWrite
{
    /// <summary>进程级按目标路径的互斥锁（大小写不敏感，Windows 语义）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_pathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取目标路径的进程级锁。所有写/删同一文件的操作都必须先取它。
    /// </summary>
    public static SemaphoreSlim GetLock(string path)
        => s_pathLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// 为一次写入生成唯一临时文件路径。
    /// <para>
    /// 唯一性是必需的：两个实例（或两个进程）并发保存同一文件时，固定名
    /// <c>*.tmp</c> 会互相覆盖/互相移动，产生 <see cref="UnauthorizedAccessException"/>。
    /// </para>
    /// </summary>
    public static string CreateTempPath(string destinationPath)
        => destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>尽力删除文件（不存在或失败均不抛出）。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // 清理失败不影响主流程；临时文件唯一，不会覆盖他人数据
        }
    }

    /// <summary>
    /// 原子替换目标文件：先重试 <see cref="File.Move(string, string, bool)"/>，
    /// 全部失败后回退为「复制覆盖 + 删除源」。
    /// </summary>
    /// <param name="sourcePath">临时文件路径（唯一）。</param>
    /// <param name="destinationPath">目标文件路径。</param>
    /// <param name="subject">日志中的主体标识（会话 ID / 组 ID）。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="IOException">重试与兜底全部失败（源文件保留，供恢复）。</exception>
    public static async Task MoveWithRetryAsync(
        string sourcePath,
        string destinationPath,
        string subject,
        ILogger? logger,
        CancellationToken ct)
    {
        const int maxRetries = 5;
        const int initialDelayMs = 50;
        var delay = initialDelayMs;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException($"临时文件不存在: {sourcePath}");

                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                logger?.LogWarning(
                    "文件移动失败 (尝试 {Attempt}/{MaxRetries}): {Subject}, 错误: {Error}",
                    attempt + 1, maxRetries, subject, ex.Message);

                if (attempt < maxRetries - 1)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay *= 2;
                }
            }
        }

        // 兜底：复制覆盖后删除源。绝不先删目标文件——即使替换失败也保住旧数据。
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            TryDelete(sourcePath);
            logger?.LogWarning("使用备用策略成功保存: {Subject}", subject);
        }
        catch (Exception ex)
        {
            // 不删除 source：调用方据成功/失败决定清理，失败时它是唯一可恢复副本
            logger?.LogError(ex, "保存失败（所有重试耗尽）: {Subject}", subject);
            throw new IOException($"保存失败: {subject}", ex);
        }
    }
}
