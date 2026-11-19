using System.Text.Json;
using Seeing.Files;
using Seeing.Serialization;

namespace Seeing.Agent.Tools.Browser;

/// <summary>
/// 客户端在宿主存活期间的存在证明：<c>~/.seeing/browser/clients/&lt;profileHash&gt;/&lt;pid&gt;.lock</c>
/// 的文件锁，加上同目录的 <c>&lt;pid&gt;.pages.json</c> 页面归属清单。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用文件锁而不是心跳</b>：客户端进程被强杀时不会执行任何清理代码，心跳会残留，
/// 宿主只能靠「多久没更新」猜测存活 —— 既有滞后（默认阈值通常几十秒，期间页面不会被回收），
/// 又有误杀（GC 停顿 / 机器休眠超过阈值）。文件锁由内核在进程死亡瞬间释放，
/// 宿主扫一遍就能得到准确结论：抢到锁 = 已死，抢不到 = 活着。
/// </para>
/// <para>
/// <b>为什么锁句柄按页面计数，而不是在 <see cref="Start"/> 时一直持有</b>：
/// 宿主用「有没有存活客户端」判断是否空闲退出。若客户端只要进程活着就一直持锁，
/// 「UI 常开但从没用过浏览器」会让宿主永不休眠。因此锁的生命周期 = 持有页面期间：
/// 第一个页面创建时加锁，最后一个页面关闭时释放；<see cref="AcquirePage"/> 可再次加锁，
/// 对象本身随进程存活。
/// </para>
/// <para>
/// <b>为什么页面清单与锁分开</b>：宿主只能看到「这个客户端死了」，
/// 它需要知道死客户端留下了哪些 target 才能逐个 <c>Target.closeTarget</c>。
/// 清单在每次开/关页后原子写，因此总是与真实页面数一致（不依赖客户端优雅退出）。
/// </para>
/// </remarks>
public sealed class BrowserClientPresence : IDisposable
{
    /// <summary>客户端锁文件后缀。</summary>
    public const string LockFileExtension = ".lock";

    /// <summary>页面归属清单文件后缀。</summary>
    public const string PagesFileExtension = ".pages.json";

    /// <summary>页面清单的 JSON 字段名（与设计文档一致：<c>{ "targetIds": [] }</c>）。</summary>
    private const string TargetIdsPropertyName = "targetIds";

    private static readonly JsonSerializerOptions s_writeOptions = SeeingJson.Create(
        indented: false,
        options => options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

    private static readonly JsonSerializerOptions s_readOptions = SeeingJson.Create(
        indented: false,
        options =>
        {
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PropertyNameCaseInsensitive = true;
        });

    private readonly object _gate = new();

    /// <summary>当前归属本客户端的 target（有序写盘，便于比对与日志）。</summary>
    private readonly HashSet<string> _targetIds = new(StringComparer.Ordinal);

    private FileStream? _lockHandle;
    private bool _wrotePages;
    private bool _disposed;

    private BrowserClientPresence(string clientsDirectory, int clientPid)
    {
        ClientsDirectory = clientsDirectory;
        ClientPid = clientPid;
    }

    /// <summary>本客户端锁 / 页面清单所在目录。</summary>
    public string ClientsDirectory { get; }

    /// <summary>本客户端 PID。</summary>
    public int ClientPid { get; }

    /// <summary>本客户端的存活锁路径。</summary>
    public string LockPath => System.IO.Path.Combine(ClientsDirectory, ClientPid + LockFileExtension);

    /// <summary>本客户端的页面归属清单路径。</summary>
    public string PagesPath => System.IO.Path.Combine(ClientsDirectory, ClientPid + PagesFileExtension);

    /// <summary>当前持有的页面数。</summary>
    public int PageCount
    {
        get
        {
            lock (_gate)
                return _targetIds.Count;
        }
    }

    /// <summary>当前持有的 target 快照（按序数排序）。</summary>
    public IReadOnlyList<string> TargetIds
    {
        get
        {
            lock (_gate)
                return Snapshot();
        }
    }

    /// <summary>
    /// 为指定 profile 启动一个客户端存在证明对象，并立即获取存活锁。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 锁随后由页面计数接管：最后一个页面关闭（1 → 0）时释放锁并删文件，
    /// 之后再次 <see cref="AcquirePage"/> 会重新加锁。因此调用方应在真正要建页时懒创建，
    /// 不要在「UI 常开但从未用过浏览器」的路径上提前调用 —— 那会让宿主永不空闲退出。
    /// </para>
    /// <para>
    /// 加锁失败（同一 PID 已有别的 presence 持锁）不在这里抛：真正的失败点是建页，
    /// <see cref="AcquirePage"/> 会明确抛出。
    /// </para>
    /// </remarks>
    /// <param name="profileDirectory">宿主 profile 目录（与宿主使用同一份绝对路径）。</param>
    public static BrowserClientPresence Start(string profileDirectory)
    {
        var locator = new BrowserHostLocator(profileDirectory);
        Directory.CreateDirectory(locator.ClientsDirectory);

        // 先加锁再返回：调用方在这一刻确实已是「准备建页的客户端」，
        // 宿主扫存活列表必须立刻能看到本进程（否则「已连上但未建页」会被判成没客户端）。
        var presence = new BrowserClientPresence(locator.ClientsDirectory, Environment.ProcessId);
        presence.TryAcquireLock();
        return presence;
    }

    /// <summary>
    /// 登记一个页面：0 → 1 时获取存活锁，随后原子写页面清单。
    /// </summary>
    /// <remarks>
    /// 同一 <paramref name="targetId"/> 重复登记是幂等的 —— 调用方（页面工厂 / 恢复路径）
    /// 可能重复上报同一个 target，重复计数会让「关掉一个页面」无法回到 0，锁就永远不释放。
    /// </remarks>
    /// <exception cref="ObjectDisposedException">对象已释放。</exception>
    /// <exception cref="IOException">无法获得存活锁（同 PID 已有其他 presence 持有）。</exception>
    public void AcquirePage(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_targetIds.Contains(targetId))
                return;

            // 先加锁再登记：加锁失败时不能留下「已登记但没锁」的状态，
            // 否则后续 ReleasePage 会去删一把属于别人的锁文件。
            // Start 已经持锁时这里是空操作（锁句柄仍然有效）。
            if (_targetIds.Count == 0)
                EnsureLock();

            _targetIds.Add(targetId);

            WritePages();
        }
    }

    /// <summary>
    /// 注销一个页面：1 → 0 时释放存活锁并删除锁文件；未登记过的 target 是空操作。
    /// </summary>
    /// <remarks>释放后再次 <see cref="AcquirePage"/> 会重新加锁（presence 对象随进程存活）。</remarks>
    public void ReleasePage(string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return;

        lock (_gate)
        {
            // 释放发生在宿主关停 / 页面回收路径上，此时对象可能已释放。
            // 抛异常会让清理路径中断（页面已经没了，锁也已经被 Dispose 释放），因此是空操作。
            if (_disposed || !_targetIds.Remove(targetId))
                return;

            WritePages();

            if (_targetIds.Count == 0)
                ReleaseLock();
        }
    }

    /// <summary>
    /// 列出指定 profile 下「仍然存活」的客户端 PID（升序）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判定方式：对每个 <c>*.lock</c> 尝试以 <see cref="FileShare.None"/> 打开 ——
    /// 打不开 = 有进程持着锁 = 存活；打得开 = 客户端已死（立刻关掉临时句柄，不删文件）。
    /// </para>
    /// <para>
    /// <b>本方法只做判定，不清理</b>：清理（关闭遗留 target、删锁与清单）属于宿主回收流程，
    /// 由宿主在持有「已确认死亡」结论后执行；查询函数顺手删文件会让调用方读到与预期不一致的状态。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> AliveClientPids(string profileDirectory)
    {
        var locator = new BrowserHostLocator(profileDirectory);
        return AliveClientPidsIn(locator.ClientsDirectory);
    }

    /// <summary>读指定客户端的页面归属清单；文件不存在或不可解析时返回空数组。</summary>
    /// <param name="profileDirectory">宿主 profile 目录。</param>
    /// <param name="clientPid">客户端 PID。</param>
    public static IReadOnlyList<string> ReadPageTargets(string profileDirectory, int clientPid)
    {
        var locator = new BrowserHostLocator(profileDirectory);
        var path = System.IO.Path.Combine(
            locator.ClientsDirectory, clientPid + PagesFileExtension);

        try
        {
            if (!File.Exists(path))
                return [];

            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize<PageManifest>(stream, s_readOptions);
            return manifest?.TargetIds ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>释放锁句柄（若仍持有），删除自己的锁与页面清单文件。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _targetIds.Clear();

            ReleaseLock();

            // 只有确实写过清单才删：同一 PID 的另一个 presence 可能拥有这份文件
            if (_wrotePages)
                AtomicFileWrite.TryDelete(PagesPath);

            // 目录里只剩别人的文件时删除会失败（IOException），忽略即可。
            // 全部客户端都退出后把空目录收掉，避免 ~/.seeing/browser/clients 无限堆积。
            TryDeleteEmptyDirectory(ClientsDirectory);
        }
    }

    private static IReadOnlyList<int> AliveClientPidsIn(string clientsDirectory)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(clientsDirectory))
                return [];

            files = Directory.GetFiles(clientsDirectory, "*" + LockFileExtension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录正好被最后一个客户端收掉 / 权限不足：本轮按「没有存活客户端」处理
            return [];
        }

        var pids = new List<int>();
        foreach (var path in files)
        {
            // 文件名即 PID（<pid>.lock）；不认得的文件直接跳过，绝不按内容猜。
            if (!int.TryParse(System.IO.Path.GetFileNameWithoutExtension(path), out var pid))
                continue;

            if (IsLockHeld(path))
                pids.Add(pid);
        }

        pids.Sort();
        return pids;
    }

    /// <summary>锁文件是否正被某个进程以 <see cref="FileShare.None"/> 持有。</summary>
    private static bool IsLockHeld(string path)
    {
        try
        {
            // 能打开 = 没有持有者 = 客户端已死。句柄立刻释放，不删文件。
            using var probe = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 扫描期间被释放并删除：按「已死」处理
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 拿不到读权限无法证明它死了；宁可少回收遗留页面，也不误判存活客户端为死
            return true;
        }
    }

    /// <summary>打开存活锁（0 → 1 页时调用；<see cref="Start"/> 已持有时为空操作）。</summary>
    private void EnsureLock()
    {
        if (_lockHandle is not null)
            return;

        try
        {
            _lockHandle = OpenLock();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 同一 PID 的文件锁是最容易踩的坑：若本进程已有另一个 presence 持锁，
            // 这里必然失败。静默继续会让宿主把本进程判成「已死」并回收页面，
            // 因此明确抛出，由调用方决定（通常意味着 presence 被重复创建）。
            throw new IOException($"无法获取浏览器客户端存活锁：{LockPath}", ex);
        }
    }

    /// <summary><see cref="Start"/> 时的尽力加锁：失败留给 <see cref="AcquirePage"/> 报错。</summary>
    private void TryAcquireLock()
    {
        try
        {
            _lockHandle ??= OpenLock();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 拿不到锁不在这里抛：对象可能只是等待重连，真正的失败点是建页
        }
    }

    private FileStream OpenLock()
        => new(
            LockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);

    /// <summary>释放存活锁并删除锁文件（1 → 0 页时调用）。</summary>
    /// <remarks>
    /// <b>没有句柄就什么都不做</b>：<see cref="Start"/> 的尽力加锁可能失败（同一 PID 已有别的
    /// presence 持锁）。此时删 <c>&lt;pid&gt;.lock</c> 会把**活着**的客户端的锁文件删掉，
    /// 宿主下一轮扫描就会把它判成已死并回收页面。
    /// </remarks>
    private void ReleaseLock()
    {
        var handle = _lockHandle;
        if (handle is null)
            return;

        _lockHandle = null;
        handle.Dispose();

        AtomicFileWrite.TryDelete(LockPath);
    }

    private string[] Snapshot()
    {
        var snapshot = new string[_targetIds.Count];
        _targetIds.CopyTo(snapshot);
        Array.Sort(snapshot, StringComparer.Ordinal);
        return snapshot;
    }

    private void WritePages()
    {
        var manifest = new PageManifest(Snapshot());
        var json = JsonSerializer.Serialize(manifest, s_writeOptions);
        AtomicFileWrite.WriteAllTextAtomic(PagesPath, json, subject: "浏览器页面归属清单");
        _wrotePages = true;
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 有别的客户端在用这个目录（或权限不足）：不是错误
        }
    }

    /// <summary>页面归属清单的落盘形状（字段名固定为 <c>targetIds</c>）。</summary>
    private sealed record PageManifest(
        [property: System.Text.Json.Serialization.JsonPropertyName(TargetIdsPropertyName)]
        string[] TargetIds);
}
