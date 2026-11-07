using Microsoft.Extensions.Logging;
using Seeing.Files;

namespace Seeing.Agent.Scheduler.Persistence;

/// <summary>
/// 调度器 JSON 文件的安全写入 — 委托给全项目唯一实现 <see cref="AtomicFileWrite"/>。
/// <para>
/// <b>为什么保留这个薄壳：</b>仓内调用点使用 <c>ReplaceAsync(source, destination, logger, ct)</c>
/// 的参数顺序（无 subject），而共享原语要求显式 subject。保留一层适配可以避免在
/// <see cref="JsonScheduleRepository"/> 等多处扩散改动；同时把「不要自行实现 temp + Move」
/// 的纪律留在原语里，这里只是转发。
/// </para>
/// <para>
/// 历史背景（不再复现）：本类曾是 <c>AtomicFileWrite</c> 的逐字复制，注释自认「照搬同一套策略」。
/// 两份实现并存意味着任何修复都要改两处，因此收敛到共享原语。
/// </para>
/// </summary>
internal static class ScheduleAtomicFile
{
    /// <summary>获取目标路径的进程级锁。同一文件的所有读-改-写都必须在它之内完成。</summary>
    public static SemaphoreSlim GetLock(string path) => AtomicFileWrite.GetLock(path);

    /// <summary>为一次写入生成唯一临时路径（避免并发方互相覆盖/互相移动）。</summary>
    public static string CreateTempPath(string destinationPath) => AtomicFileWrite.CreateTempPath(destinationPath);

    /// <summary>尽力删除文件：不存在或失败都不抛出。</summary>
    public static void TryDelete(string path) => AtomicFileWrite.TryDelete(path);

    /// <summary>
    /// 原子替换目标文件：先重试移动，全部失败后回退为「复制覆盖 + 删除源」。
    /// </summary>
    /// <param name="sourcePath">唯一临时文件路径。</param>
    /// <param name="destinationPath">目标文件路径。</param>
    /// <param name="logger">日志。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="IOException">重试与兜底全部失败（源文件保留，供恢复）。</exception>
    public static Task ReplaceAsync(
        string sourcePath,
        string destinationPath,
        ILogger logger,
        CancellationToken ct)
        => AtomicFileWrite.MoveWithRetryAsync(
            sourcePath,
            destinationPath,
            subject: Path.GetFileName(destinationPath),
            logger,
            ct);
}
