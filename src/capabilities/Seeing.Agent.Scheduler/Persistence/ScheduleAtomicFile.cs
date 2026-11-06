using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Seeing.Agent.Scheduler.Persistence;

/// <summary>
/// 调度器 JSON 文件的安全写入：跨实例路径锁 + 唯一临时文件名 + 移动重试（复制兜底）。
/// <para>
/// 为什么需要它：<c>AppendHistoryAsync</c> 是「读-改-写」流程，互斥必须覆盖**整个周期**；
/// 而锁如果挂在仓储实例上，多个实例（DI 单例 + CLI、冷/热实例、测试并行）各持一把，
/// 等于没有互斥。
/// </para>
/// <para>
/// 两个缺陷叠加会造成现场那个 bug：
/// </para>
/// <list type="number">
/// <item>临时文件名固定为 <c>{path}.tmp</c> —— 两方写同一个文件，一方 <c>File.Move</c>
/// 消费掉后，另一方永远等不到它（重试再多次也是 <see cref="FileNotFoundException"/>）。</item>
/// <item>锁是实例级的 —— 实例之间完全并发，连上面的覆盖都不会被挡住。</item>
/// </list>
/// <para>
/// 与 <c>Seeing.Session.Storage.AtomicFileWrite</c> 同构：那边已经因为同样的原因
/// 采用「进程级按路径锁 + GUID 临时名」，此处照搬同一套策略。
/// </para>
/// </summary>
internal static class ScheduleAtomicFile
{
    /// <summary>进程级按目标路径的互斥锁（大小写不敏感，匹配 Windows 语义）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_pathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 获取目标路径的进程级锁。同一文件的所有读-改-写都必须在它之内完成。
    /// </summary>
    public static SemaphoreSlim GetLock(string path) =>
        s_pathLocks.GetOrAdd(NormalizeKey(path), static _ => new SemaphoreSlim(1, 1));

    /// <summary>为一次写入生成唯一临时路径（避免并发方互相覆盖/互相移动）。</summary>
    public static string CreateTempPath(string destinationPath) =>
        destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

    /// <summary>尽力删除文件：不存在或失败都不抛出。</summary>
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
    /// 全部失败后回退为「复制覆盖 + 删除源」，绝不先删目标文件。
    /// </summary>
    /// <param name="sourcePath">唯一临时文件路径。</param>
    /// <param name="destinationPath">目标文件路径。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="IOException">重试与兜底全部失败（源文件保留，供恢复）。</exception>
    public static async Task ReplaceAsync(
        string sourcePath,
        string destinationPath,
        ILogger logger,
        CancellationToken ct)
    {
        const int maxAttempts = 5;
        const int initialDelayMs = 20;
        var delay = initialDelayMs;

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
                    logger.LogWarning(
                        "替换文件失败（{Attempt} 次尝试耗尽），改用复制兜底: {Path}, 错误: {Error}",
                        attempt, destinationPath, ex.Message);
                    break;
                }

                logger.LogDebug(
                    "替换文件失败（尝试 {Attempt}/{MaxAttempts}）: {Path}, 错误: {Error}",
                    attempt, maxAttempts, destinationPath, ex.Message);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay *= 2;
            }
        }

        // 兜底：复制覆盖后删除源。绝不先删目标——即使替换失败也保住旧数据。
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            TryDelete(sourcePath);
            logger.LogWarning("使用复制兜底成功写入: {Path}", destinationPath);
        }
        catch (Exception ex)
        {
            // 不删 source：失败时它是唯一可恢复副本
            logger.LogError(ex, "写入失败（所有策略耗尽）: {Path}", destinationPath);
            throw new IOException($"写入失败: {destinationPath}", ex);
        }
    }

    /// <summary>
    /// 规范化锁键：转绝对路径并统一分隔符，避免同一文件因写法不同拿到两把锁。
    /// </summary>
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
