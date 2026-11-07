using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Seeing.Files;

/// <summary>
/// 原子文件替换原语：进程级按路径锁 + 唯一临时文件名 + 移动重试（复制兜底）。
/// <para>
/// <b>为什么需要它：</b><c>File.Move(overwrite: true)</c> 在 Windows 上会因 Defender 扫描、
/// 文件系统缓存未刷新、其它句柄短暂占用而抛出 <see cref="UnauthorizedAccessException"/>
/// 或 <see cref="IOException"/>。这类失败是瞬时的，重试即可；重试耗尽后改用
/// 「先复制覆盖、再删除源」的兜底，保证目标文件不会被提前毁掉。
/// </para>
/// <para>
/// <b>为什么锁必须进程级：</b>同一个文件可能被多个实例（DI 单例 + CLI、冷/热实例、测试并行）
/// 或同进程多消费者同时写。实例级锁无法互斥；配合固定临时名 <c>{path}.tmp</c> 时，
/// 一方 <c>Move</c> 消费掉临时文件后，另一方重试再多次也只会得到
/// <see cref="FileNotFoundException"/>——这是历史上多次「配置莫名回退 / 文件丢失」的成因。
/// </para>
/// <para>
/// <b>本项目其余包不得自行实现「temp + Move」</b>：持久化文件写入一律走本类。
/// </para>
/// </summary>
public static class AtomicFileWrite
{
    /// <summary>进程级按目标路径的互斥锁（大小写不敏感，匹配 Windows 语义）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_pathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取目标路径的进程级锁。所有写/删同一文件的读-改-写周期都必须先取它，
    /// 并覆盖**整个周期**（读取 → 修改 → 替换），而不只是替换那一步。
    /// </summary>
    public static SemaphoreSlim GetLock(string path) =>
        s_pathLocks.GetOrAdd(NormalizeKey(path), static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// 为一次写入生成唯一的临时文件路径。
    /// <para>唯一性是必需的：两个实例或两个进程并发保存同一文件时，固定名会互相覆盖/互相移动。</para>
    /// </summary>
    public static string CreateTempPath(string destinationPath) =>
        destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

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
    /// 全部失败后回退为「复制覆盖 + 删除源」（绝不先删目标文件）。
    /// </summary>
    /// <param name="sourcePath">临时文件路径（应来自 <see cref="CreateTempPath"/>，全局唯一）。</param>
    /// <param name="destinationPath">目标文件路径。</param>
    /// <param name="subject">日志中的主体标识（会话 ID / 配置名 / 文件名）。</param>
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
        const int maxAttempts = 5;
        const int initialDelayMs = 50;
        var delay = initialDelayMs;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
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
                if (attempt == maxAttempts)
                {
                    logger?.LogWarning(
                        "替换文件失败（{Attempt} 次尝试耗尽），改用复制兜底: {Subject}, 错误: {Error}",
                        attempt, subject, ex.Message);
                    break;
                }

                logger?.LogDebug(
                    "替换文件失败（尝试 {Attempt}/{MaxAttempts}）: {Subject}, 错误: {Error}",
                    attempt, maxAttempts, subject, ex.Message);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }

        // 兜底：复制覆盖后删除源。绝不先删目标文件——即使替换失败也保住旧数据。
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            TryDelete(sourcePath);
            logger?.LogWarning("使用复制兜底成功写入: {Subject}", subject);
        }
        catch (Exception ex)
        {
            // 不删 source：失败时它是唯一可恢复副本
            logger?.LogError(ex, "保存失败（所有策略耗尽）: {Subject}", subject);
            throw new IOException($"保存失败: {subject}", ex);
        }
    }

    /// <summary>
    /// 同步原子写字节：唯一临时名 → 重试移动 → 复制兜底。
    /// <para>供同步路径（如密钥生成）使用；与 <see cref="WriteAllTextAtomic"/> 同构。</para>
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">字节内容。</param>
    /// <param name="subject">日志中的主体标识。</param>
    /// <param name="logger">可选日志。</param>
    public static void WriteAllBytesAtomic(string path, byte[] content, string subject, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = CreateTempPath(path);
        try
        {
            File.WriteAllBytes(tempPath, content);
            ReplaceWithRetry(tempPath, path, subject, logger);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            logger?.LogError(ex, "保存失败（所有策略耗尽）: {Subject}", subject);
            throw new IOException($"保存失败: {subject}", ex);
        }
    }

    /// <summary>同步替换：重试移动 → 复制兜底（不删除失败源文件）。</summary>
    private static void ReplaceWithRetry(string sourcePath, string destinationPath, string subject, ILogger? logger)
    {
        const int maxAttempts = 5;
        var delayMs = 20;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == maxAttempts)
                {
                    logger?.LogWarning(
                        "替换文件失败（{Attempt} 次尝试耗尽），改用复制兜底: {Subject}, 错误: {Error}",
                        attempt, subject, ex.Message);
                    break;
                }

                Thread.Sleep(delayMs);
                delayMs *= 2;
            }
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
        TryDelete(sourcePath);
        logger?.LogWarning("使用复制兜底成功写入: {Subject}", subject);
    }

    /// <summary>
    /// 同步原子写文本：唯一临时名 → 重试移动 → 复制兜底。
    /// <para>
    /// <b>本方法不取路径锁</b>：它只保证「单次写入不会留下半截文件」。
    /// 若语义是「读 → 改 → 写」的完整周期，调用方必须先自行
    /// <see cref="GetLock"/> 包住整个周期（否则两个实例会各自基于旧内容覆盖对方）。
    /// 异步上下文请用 <see cref="CreateTempPath"/> + <see cref="MoveWithRetryAsync"/>。
    /// </para>
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    /// <param name="content">文本内容。</param>
    /// <param name="subject">日志中的主体标识。</param>
    /// <param name="logger">可选日志。</param>
    public static void WriteAllTextAtomic(string path, string content, string subject, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = CreateTempPath(path);
        try
        {
            File.WriteAllText(tempPath, content);
            ReplaceWithRetry(tempPath, path, subject, logger);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            logger?.LogError(ex, "保存失败（所有策略耗尽）: {Subject}", subject);
            throw new IOException($"保存失败: {subject}", ex);
        }
    }

    /// <summary>规范化锁键：转绝对路径并统一分隔符，避免同一文件因写法不同拿到两把锁。</summary>
    private static string NormalizeKey(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            full = path;
        }

        return full.Replace('\\', '/');
    }
}
