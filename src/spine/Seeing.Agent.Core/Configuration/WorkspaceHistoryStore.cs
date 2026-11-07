using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;
using Seeing.Files;
using Seeing.Serialization;

namespace Seeing.Agent.Core.Configuration;

/// <summary>
/// 最近工作区历史。跨进程用命名互斥量串行化，进程内再持有 <see cref="AtomicFileWrite.GetLock"/>。
/// <para>
/// 为什么 <c>recent</c> 要按路径合并：两个实例各自读到同一份旧快照时，
/// 后写者整文件覆盖会让先写者刚加入的目录凭空消失。合并只对 <c>recent</c> 生效；
/// <c>lastInteractive</c> 是「最后一次界面选择」，后写者胜出才符合语义。
/// </para>
/// </summary>
public sealed class WorkspaceHistoryStore : IWorkspaceHistoryStore
{
    private const int MaxRecent = 30;
    private const string MutexName = @"Local\Seeing.Agent.WorkspaceHistory";
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(5);

    private readonly string _filePath;
    private readonly ILogger<WorkspaceHistoryStore>? _logger;
    private readonly Func<string, bool> _directoryExists;

    /// <summary>创建历史存储。</summary>
    /// <param name="historyFilePath">历史文件绝对路径。</param>
    /// <param name="logger">可选日志。</param>
    /// <param name="directoryExists">目录存在性判定（注入以便测试）。</param>
    public WorkspaceHistoryStore(
        string historyFilePath,
        ILogger<WorkspaceHistoryStore>? logger = null,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyFilePath);
        _filePath = historyFilePath;
        _logger = logger;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    /// <inheritdoc/>
    public WorkspaceHistorySnapshot Read()
    {
        var data = Load();
        return new WorkspaceHistorySnapshot(data.Recent, data.LastInteractive);
    }

    /// <inheritdoc/>
    public void RecordOpened(string path, bool asDefault)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // 进程内锁必须先拿：同一进程的多个 circuit 会并发写。
        var gate = AtomicFileWrite.GetLock(_filePath);
        gate.Wait();
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, MutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(MutexTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个持有者崩溃：互斥量已归我们，继续即可。
                    ownsMutex = true;
                }

                var data = Load();

                var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                var recent = new List<string> { normalized };
                recent.AddRange(data.Recent.Where(p => !IsSame(p, normalized)));

                var lastInteractive = asDefault ? normalized : data.LastInteractive;

                Save(new HistoryFile
                {
                    Recent = recent.Take(MaxRecent).ToList(),
                    LastInteractive = lastInteractive,
                });
            }
            finally
            {
                if (ownsMutex)
                    mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "写入工作区历史失败: {Path}", path);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var gate = AtomicFileWrite.GetLock(_filePath);
        gate.Wait();
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, MutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = mutex.WaitOne(MutexTimeout);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                var data = Load();
                var remaining = data.Recent.Where(p => !IsSame(p, path)).ToList();
                var lastInteractive = data.LastInteractive is not null && IsSame(data.LastInteractive, path)
                    ? null
                    : data.LastInteractive;

                Save(new HistoryFile { Recent = remaining, LastInteractive = lastInteractive });
            }
            finally
            {
                if (ownsMutex)
                    mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "移除工作区历史失败: {Path}", path);
        }
        finally
        {
            gate.Release();
        }
    }

    private HistoryFile Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new HistoryFile();

            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return new HistoryFile();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // 旧格式：字符串数组，最前为最新。
                var legacy = root.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();

                return new HistoryFile
                {
                    Recent = legacy,
                    LastInteractive = legacy.FirstOrDefault(_directoryExists),
                };
            }

            if (root.ValueKind != JsonValueKind.Object)
                return new HistoryFile();

            var parsed = JsonSerializer.Deserialize<HistoryFile>(json);
            if (parsed is null)
                return new HistoryFile();

            return new HistoryFile
            {
                Recent = parsed.Recent.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
                LastInteractive = parsed.LastInteractive,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取工作区历史失败，按空历史处理: {Path}", _filePath);
            return new HistoryFile();
        }
    }

    private void Save(HistoryFile data)
    {
        // 不存在的目录不留：下次启动也不该尝试进入它们。
        data.Recent = data.Recent.Where(_directoryExists).ToList();
        if (data.LastInteractive is not null && !_directoryExists(data.LastInteractive))
            data.LastInteractive = null;

        var json = JsonSerializer.Serialize(data, SeeingJson.ReadableIndented);
        AtomicFileWrite.WriteAllTextAtomic(_filePath, json, "workspace_history", _logger);
    }

    private static bool IsSame(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathBoundary.PathComparison);

    private sealed class HistoryFile
    {
        [JsonPropertyName("recent")]
        public List<string> Recent { get; set; } = new();

        [JsonPropertyName("lastInteractive")]
        public string? LastInteractive { get; set; }
    }
}
