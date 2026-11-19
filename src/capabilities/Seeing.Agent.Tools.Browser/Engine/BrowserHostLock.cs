using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 宿主单例锁：<c>&lt;profile&gt;/.browser-host.lock</c>（<see cref="FileMode.OpenOrCreate"/>
/// + <see cref="FileShare.None"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用文件锁而不是 PID 文件</b>：PID 文件在进程被强杀 / 崩溃后一定残留，
/// 需要额外的「这个 PID 还活着吗、还是被复用了」判断，而文件锁由内核在进程死亡时释放 ——
/// 没有探测、没有超时、没有误判窗口。
/// </para>
/// <para>
/// <b>为什么锁文件永不删除</b>：删除锁文件会引入 unlink 竞态 ——
/// 进程 A 释放（关闭句柄）后删掉文件，进程 B 恰好在删除前打开了旧 inode 并拿到锁，
/// 之后进程 C 又创建同一个名字的新文件并再拿一次锁，于是 B 与 C 同时认为自己是唯一宿主。
/// 文件留着不删，所有竞争者永远抢同一个 inode，而「是否被占用」只由句柄决定。
/// </para>
/// <para>
/// <b>为什么拿不到锁直接返回 <see langword="null"/> 而不是抛</b>：这是正常的「已有宿主在跑」，
/// 调用方（宿主启动器）应当安静退出（退出码 0），而不是把用户界面打成红色错误。
/// </para>
/// <para>
/// <b>两种失败必须分开记日志</b>：「已被占用」是正常竞争（Debug），
/// 「目录不可写」是配置 / 权限故障（Warning）—— 两者都返回 <see langword="null"/>，
/// 但排查方向完全相反：前者说明「本该连上现有宿主」，后者说明「宿主根本起不来」。
/// </para>
/// </remarks>
public sealed class BrowserHostLock : IDisposable
{
    /// <summary>锁文件名（固定放在 profile 目录根下）。</summary>
    public const string FileName = ".browser-host.lock";

    private readonly string _path;
    private readonly ILogger? _logger;

    private FileStream? _handle;

    private BrowserHostLock(string path, FileStream handle, ILogger? logger)
    {
        _path = path;
        _handle = handle;
        _logger = logger;
    }

    /// <summary>锁文件完整路径。</summary>
    public string Path => _path;

    /// <summary>是否仍持有锁句柄。</summary>
    public bool IsHeld => Volatile.Read(ref _handle) is not null;

    /// <summary>
    /// 尝试获取指定 profile 的宿主单例锁；已被占用或目录不可用时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="profileDirectory">宿主 profile 目录（不存在会自动创建）。</param>
    /// <param name="logger">日志（可选）。</param>
    public static BrowserHostLock? TryAcquire(string profileDirectory, ILogger? logger = null)
    {
        var directory = BrowserHostLocator.NormalizeProfileDirectory(profileDirectory);
        var path = System.IO.Path.Combine(directory, FileName);

        try
        {
            Directory.CreateDirectory(directory);

            // 句柄必须活到 Dispose：FileShare.None 的占用语义由**打开着的句柄**承载。
            var handle = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            logger?.LogDebug("已获取宿主单例锁：{Path}", path);
            return new BrowserHostLock(path, handle, logger);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 目录 / 锁文件不可写：宿主即使拿到锁也无法写发现文件与 profile，
            // 这是配置或权限故障，与「已有宿主在跑」完全不同。
            logger?.LogWarning(
                ex,
                "宿主单例锁无法创建（目录不可写或无权限），本次不作为宿主启动：{Path}",
                path);
            return null;
        }
        catch (IOException ex)
        {
            // 正常竞争：另一个宿主已以 FileShare.None 持有该锁
            logger?.LogDebug(
                ex,
                "宿主单例锁已被其他进程占用，本次不作为宿主启动：{Path}",
                path);
            return null;
        }
    }

    /// <summary>释放锁句柄。**不删除锁文件**（见类型注释的 unlink 竞态）。</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null)
            return;

        handle.Dispose();
        _logger?.LogDebug("已释放宿主单例锁：{Path}", _path);
    }
}
