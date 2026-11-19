using System.Globalization;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 回收「已死客户端」留下的页面与归属清单。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么按 targetId 关页面而不是按客户端</b>：客户端崩溃时不会执行任何清理代码，
/// 唯一可靠的事实是它写下的 <c>pages.json</c>。<c>Target.closeTarget</c> 是浏览器级操作，
/// 不需要客户端连接存在。
/// </para>
/// <para>
/// <b>为什么删除锁文件是安全的</b>：扫描前刚刚判定该 PID 已死
/// （<see cref="BrowserClientPresence.AliveClientPids"/>）。若恰好在扫描与删除之间
/// 该 PID 被复用并重新持锁，<c>File.Delete</c> 在 Windows 上会因共享冲突失败，
/// 这里只记 Debug 并跳过 —— 不能把活客户端的锁删掉。
/// </para>
/// <para>
/// <b>为什么放在能力包而不是宿主运行器里</b>：它只依赖「浏览器连接 + 发现文件目录」两个
/// 能力包概念，与宿主进程编排无关。放在这里才能被真实浏览器的集成测试直接调用；
/// 若留在 <c>samples</c> 的宿主运行器里，测试只能靠「等宿主主循环 30 秒」间接覆盖，
/// 那条路径实际上等于没有测试。
/// </para>
/// </remarks>
public static class BrowserClientReclaimer
{
    /// <summary>
    /// 扫描客户端锁目录，关闭死客户端登记的 target 并删除其锁与清单。
    /// </summary>
    /// <param name="browser">宿主持有的浏览器连接（只读 client 的页面，不关浏览器）。</param>
    /// <param name="locator">发现文件定位器（客户端锁目录由它派生）。</param>
    /// <param name="logger">日志（可空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次关闭的 target 数（诊断与测试用）。</returns>
    public static async Task<int> ReclaimAsync(
        IBrowser browser,
        BrowserHostLocator locator,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(locator);

        if (!Directory.Exists(locator.ClientsDirectory))
            return 0;

        IReadOnlyList<int> alivePids;
        try
        {
            alivePids = BrowserClientPresence.AliveClientPids(
                locator.ProfileDirectory, locator.BaseDirectory, logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "扫描存活客户端失败（本轮跳过回收）");
            return 0;
        }

        var alive = new HashSet<int>(alivePids);
        string[] lockFiles;
        try
        {
            lockFiles = Directory.GetFiles(
                locator.ClientsDirectory, "*" + BrowserClientPresence.LockFileExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "枚举客户端锁失败（本轮跳过回收）");
            return 0;
        }

        var closed = 0;
        ICDPSession? session = null;
        try
        {
            foreach (var lockFile in lockFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!int.TryParse(
                        Path.GetFileNameWithoutExtension(lockFile),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var clientPid)
                    || clientPid <= 0
                    || alive.Contains(clientPid))
                {
                    continue;
                }

                var targets = BrowserClientPresence.ReadPageTargets(
                    locator.ProfileDirectory, locator.BaseDirectory, clientPid, logger);

                if (targets.Count > 0)
                {
                    session ??= await browser.Target.CreateCDPSessionAsync().ConfigureAwait(false);

                    foreach (var targetId in targets)
                    {
                        if (string.IsNullOrWhiteSpace(targetId))
                            continue;

                        try
                        {
                            await session.SendAsync("Target.closeTarget", new { targetId })
                                .ConfigureAwait(false);
                            closed++;
                            logger?.LogInformation(
                                "已关闭死客户端 {ClientPid} 的遗留页面 {TargetId}", clientPid, targetId);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // 页面可能已经被用户手动关掉 / target 已失效：继续处理其余 target。
                            logger?.LogDebug(ex, "关闭遗留页面 {TargetId} 失败", targetId);
                        }
                    }
                }

                DeleteClientFiles(locator.ClientsDirectory, clientPid, logger);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogDebug(ex, "回收死客户端页面时失败（下一轮继续）");
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.DetachAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 浏览器可能已经断开：detach 失败不影响宿主主循环。
                    logger?.LogDebug(ex, "释放 CDP 会话失败");
                }
            }
        }

        return closed;
    }

    /// <summary>删除死客户端的锁与页面清单（删不掉只记日志：可能是 PID 被复用后重新持锁）。</summary>
    private static void DeleteClientFiles(string clientsDirectory, int clientPid, ILogger? logger)
    {
        var lockPath = Path.Combine(clientsDirectory, clientPid + BrowserClientPresence.LockFileExtension);
        var pagesPath = Path.Combine(clientsDirectory, clientPid + BrowserClientPresence.PagesFileExtension);

        foreach (var path in new[] { pagesPath, lockPath })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 锁仍在被持有（PID 被复用）/ 权限不足：保留文件比误删活客户端的锁安全。
                logger?.LogDebug(ex, "删除死客户端文件失败：{Path}", path);
            }
        }
    }
}
