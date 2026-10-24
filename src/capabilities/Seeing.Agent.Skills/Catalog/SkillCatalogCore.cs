using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// <see cref="ISkillCatalog"/> 的唯一实现：不可变快照 + 单调版本 + 变更事件。
/// <para>双通道：<c>_registered</c>（插件 / 内嵌资源，优先级最高）与 <c>_fileSkills</c>（文件系统，
/// 按根优先级去重）。快照构建后用 <see cref="Volatile.Write{T}(ref T, T)"/> 原子替换，
/// 读者永远看不到空窗（<see cref="Current"/> 读到的要么是旧快照、要么是新快照）。</para>
/// <para>刷新单飞：并发调用在等锁期间若观察到版本已推进，直接返回当前快照，不重复扫盘。</para>
/// </summary>
public sealed class SkillCatalogCore : ISkillCatalog, IDisposable
{
    /// <summary>状态文件名（沿用既有 <c>skill-state.json</c>）。</summary>
    private const string SkillStateFileName = "skill-state.json";

    private readonly ILogger<SkillCatalogCore> _logger;
    private readonly ISeeingDirectories? _directories;

    /// <summary>扫盘闸：同一时刻只允许一次扫盘（单飞的判据是快照实例 + 版本推进）。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 发布闸：快照字段写入 + 原子替换的唯一临界区。
    /// <para>用同步锁而非异步闸，使注册通道与扫盘共用同一临界区，
    /// 既不会与扫盘交错产出半更新快照，也不需要同步等待异步闸。</para>
    /// </summary>
    private readonly object _publishLock = new();

    private readonly object _subscriberLock = new();
    private readonly List<Action<SkillCatalogSnapshot>> _subscribers = [];
    private readonly object _rootsLock = new();

    /// <summary>注册通道（不可变字典，按名称索引）。</summary>
    private ImmutableDictionary<string, SkillCatalogEntry> _registered =
        ImmutableDictionary.Create<string, SkillCatalogEntry>(StringComparer.OrdinalIgnoreCase);

    /// <summary>文件通道（不可变字典，按名称索引）。</summary>
    private ImmutableDictionary<string, SkillCatalogEntry> _fileSkills =
        ImmutableDictionary.Create<string, SkillCatalogEntry>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<SkillConflict> _fileConflicts = [];
    private IReadOnlyList<SkillLoadIssue> _fileIssues = [];
    private IReadOnlySet<string> _disabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ConfigLevel> _disabledScopes =
        new Dictionary<string, ConfigLevel>(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前快照（唯一权威引用，替换靠 <see cref="Volatile.Write{T}(ref T, T)"/>）。</summary>
    private SkillCatalogSnapshot _snapshot = CreateEmptySnapshot();

    /// <summary>最近一次扫盘的文件指纹（<see cref="EnsureFreshAsync"/> 的廉价判据）。</summary>
    private string? _fingerprint;

    private IReadOnlyList<SkillRoot> _roots;

    /// <summary>
    /// 创建技能目录。
    /// </summary>
    /// <param name="logger">目录日志。</param>
    /// <param name="directories">配置目录契约；为 null 时无法定位默认根与禁用状态（测试 / 极简宿主）。</param>
    public SkillCatalogCore(ILogger<SkillCatalogCore> logger, ISeeingDirectories? directories = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _directories = directories;
        _roots = BuildDefaultRoots();
    }

    /// <inheritdoc />
    public SkillCatalogSnapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>
    /// 默认根目录（优先级从高到低）：<c>{ws}/.seeing/skills</c>、<c>{ws}/.agents/skills</c>、
    /// <c>{ws}/skills</c>、<c>~/.seeing/skills</c>、<c>~/.agents/skills</c>。
    /// <para>加载器据此计算根列表，避免「默认根」出现第二份定义。</para>
    /// </summary>
    public IReadOnlyList<string> DefaultRoots =>
        BuildDefaultRoots().Select(root => root.Directory).ToArray();

    /// <summary>当前生效的根目录（优先级从高到低）。</summary>
    public IReadOnlyList<string> Roots =>
        Volatile.Read(ref _roots).Select(root => root.Directory).ToArray();

    /// <inheritdoc />
    public IDisposable Subscribe(Action<SkillCatalogSnapshot> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        lock (_subscriberLock)
            _subscribers.Add(onChanged);

        return new Subscription(this, onChanged);
    }

    /// <inheritdoc />
    public SkillCatalogEntry? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (var entry in Current.Entries)
        {
            if (string.Equals(entry.Info.Name, name, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsEnabled(string name) =>
        Find(name) is not null && !Current.DisabledNames.Contains(name);

    /// <inheritdoc />
    public async Task<SkillCatalogSnapshot> EnsureFreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var roots = Volatile.Read(ref _roots);
        var fingerprint = await ComputeFingerprintAsync(roots, ct).ConfigureAwait(false);
        if (string.Equals(fingerprint, Volatile.Read(ref _fingerprint), StringComparison.Ordinal))
            return Current;

        return await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 单飞判据：进入时的快照实例；等锁期间若已被其它调用推进，直接复用其成果
        var observed = Current;

        SkillCatalogSnapshot snapshot;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = ReferenceEquals(Current, observed)
                ? await ScanAndPublishAsync(ct).ConfigureAwait(false)
                : Current;
        }
        finally
        {
            _gate.Release();
        }

        // 事件只在锁外触发，且只有真正发布了新快照才触发（并发共享者不重复触发）
        if (!ReferenceEquals(snapshot, observed))
            RaiseChanged(snapshot);

        return snapshot;
    }

    /// <summary>扫盘 → 合并 → 发布。</summary>
    private async Task<SkillCatalogSnapshot> ScanAndPublishAsync(CancellationToken ct)
    {
        var roots = Volatile.Read(ref _roots);
        var fingerprint = await ComputeFingerprintAsync(roots, ct).ConfigureAwait(false);
        var scan = await SkillFileScanner.ScanAsync(roots, _logger, ct).ConfigureAwait(false);
        var (disabledNames, disabledScopes) = await LoadDisabledStateAsync(ct).ConfigureAwait(false);

        lock (_publishLock)
        {
            _fileSkills = ToImmutable(scan.Entries);
            _fileConflicts = scan.Conflicts.ToArray();
            _fileIssues = scan.Issues.ToArray();
            _disabledNames = disabledNames;
            _disabledScopes = disabledScopes;
            Volatile.Write(ref _fingerprint, fingerprint);

            return Publish();
        }
    }

    /// <inheritdoc />
    public Task<bool> RegisterAsync(
        SkillInfo info,
        string providerId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var accepted = TryRegisterCore(info, providerId, out var published);
        if (published is not null)
            RaiseChanged(published);

        return Task.FromResult(accepted);
    }

    private bool TryRegisterCore(SkillInfo info, string providerId, out SkillCatalogSnapshot? published)
    {
        ArgumentNullException.ThrowIfNull(info);
        published = null;

        if (string.IsNullOrWhiteSpace(info.Name))
        {
            _logger.LogWarning("拒绝注册名称无效的技能：提供方={ProviderId}", providerId);
            return false;
        }

        lock (_publishLock)
        {
            if (_registered.ContainsKey(info.Name))
            {
                _logger.LogWarning(
                    "技能名已被注册，拒绝重复注册：{Name} 提供方={ProviderId}",
                    info.Name,
                    providerId);
                return false;
            }

            var entry = new SkillCatalogEntry(
                info,
                SkillSource.Registered,
                providerId,
                DirectoryPathOf(info));

            _registered = _registered.SetItem(info.Name, entry);
            published = Publish();
            _logger.LogInformation("已注册技能：{Name} 提供方={ProviderId}", info.Name, providerId);
            return true;
        }
    }

    /// <inheritdoc />
    public Task<bool> UnregisterAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var removed = TryUnregisterCore(name, out var published);
        if (published is not null)
            RaiseChanged(published);

        return Task.FromResult(removed);
    }

    private bool TryUnregisterCore(string name, out SkillCatalogSnapshot? published)
    {
        published = null;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (_publishLock)
        {
            if (!_registered.ContainsKey(name))
                return false;

            _registered = _registered.Remove(name);
            published = Publish();
            _logger.LogInformation("已注销技能：{Name}", name);
            return true;
        }
    }

    /// <summary>
    /// 替换文件通道的根列表（优先级从高到低）。供宿主 / 加载器切换工作区时重挂。
    /// <para>只替换根并作废指纹，不在此处扫盘；下一次 <see cref="EnsureFreshAsync"/> 会重建。</para>
    /// </summary>
    public void SetRoots(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var projectAnchor = ProjectAnchor();
        var classified = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => new SkillRoot(root, ClassifySource(root, projectAnchor)))
            .ToArray();

        Volatile.Write(ref _roots, classified);
        Volatile.Write(ref _fingerprint, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_subscriberLock)
            _subscribers.Clear();

        _gate.Dispose();
    }

    // ===== 快照发布 =====

    /// <summary>
    /// 合并双通道并发布新快照（版本 +1），最后在锁外触发事件。
    /// <para>注册通道优先：同名文件技能被注册项遮蔽，并记录一条冲突（不静默覆盖）。</para>
    /// <para>调用方必须持有 <see cref="_publishLock"/>：快照字段读取与版本推进同属一个临界区。</para>
    /// </summary>
    private SkillCatalogSnapshot Publish()
    {
        var entries = new List<SkillCatalogEntry>(_registered.Count + _fileSkills.Count);
        var conflicts = new List<SkillConflict>(_fileConflicts.Count + _registered.Count);

        // 注册通道：名称稳定排序，保证快照内容与顺序可复现
        foreach (var entry in _registered.Values.OrderBy(e => e.Info.Name, StringComparer.Ordinal))
            entries.Add(entry);

        // 文件通道：按扫描顺序（根优先级），被注册同名遮蔽者记冲突
        foreach (var entry in _fileSkills.Values.OrderBy(e => e.Info.Name, StringComparer.Ordinal))
            entries.Add(entry);

        conflicts.AddRange(_fileConflicts);

        foreach (var registered in _registered.Values)
        {
            if (!_fileSkills.TryGetValue(registered.Info.Name, out var shadowed))
                continue;

            conflicts.Add(new SkillConflict(
                registered.Info.Name,
                SkillSource.Registered,
                registered.ProviderId ?? "registered",
                shadowed.Source,
                shadowed.DirectoryPath));
        }

        var enabledEntries = new List<SkillCatalogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (_registered.ContainsKey(entry.Info.Name) && entry.Source != SkillSource.Registered)
                continue; // 被注册项遮蔽的文件条目：不可调用

            enabledEntries.Add(entry);
        }

        var version = Current.Version + 1;
        var snapshot = new SkillCatalogSnapshot(
            version,
            enabledEntries,
            _disabledNames,
            _disabledScopes,
            conflicts,
            _fileIssues);

        Volatile.Write(ref _snapshot, snapshot);

        _logger.LogDebug(
            "技能目录快照已发布：版本={Version} 条目={EntryCount} 冲突={ConflictCount} 问题={IssueCount}",
            snapshot.Version,
            snapshot.Entries.Count,
            snapshot.Conflicts.Count,
            snapshot.Issues.Count);

        return snapshot;
    }

    private void RaiseChanged(SkillCatalogSnapshot snapshot)
    {
        Action<SkillCatalogSnapshot>[] handlers;
        lock (_subscriberLock)
            handlers = _subscribers.ToArray();

        foreach (var handler in handlers)
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception ex)
            {
                // 订阅方异常不得污染目录：记录并继续通知其它订阅者
                _logger.LogWarning(ex, "技能目录订阅回调失败：版本={Version}", snapshot.Version);
            }
        }
    }

    private static SkillCatalogSnapshot CreateEmptySnapshot() =>
        new(
            0,
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ConfigLevel>(StringComparer.OrdinalIgnoreCase),
            [],
            []);

    private static ImmutableDictionary<string, SkillCatalogEntry> ToImmutable(
        IReadOnlyList<SkillCatalogEntry> entries)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, SkillCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            builder[entry.Info.Name] = entry;

        return builder.ToImmutable();
    }

    private static string DirectoryPathOf(SkillInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Location))
            return string.Empty;

        return Path.GetDirectoryName(info.Location) ?? string.Empty;
    }

    // ===== 根 =====

    /// <summary>
    /// 默认根（优先级从高到低）：<c>{ws}/.seeing/skills</c>、<c>{ws}/.agents/skills</c>、
    /// <c>{ws}/skills</c>、<c>~/.seeing/skills</c>、<c>~/.agents/skills</c>。
    /// </summary>
    private IReadOnlyList<SkillRoot> BuildDefaultRoots()
    {
        var roots = new List<SkillRoot>();

        var projectAnchor = ProjectAnchor();
        if (projectAnchor is not null)
        {
            roots.Add(new SkillRoot(
                Path.Combine(_directories!.ProjectSeeingDirectory, "skills"),
                SkillSource.ProjectFile));
            roots.Add(new SkillRoot(
                Path.Combine(projectAnchor, ".agents", "skills"),
                SkillSource.ProjectFile));
            roots.Add(new SkillRoot(
                Path.Combine(projectAnchor, "skills"),
                SkillSource.ProjectFile));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            roots.Add(new SkillRoot(Path.Combine(userProfile, ".seeing", "skills"), SkillSource.UserFile));
            roots.Add(new SkillRoot(Path.Combine(userProfile, ".agents", "skills"), SkillSource.UserFile));
        }

        return roots;
    }

    /// <summary>项目根（<c>{root}/.seeing</c> 的父目录）；无目录契约时为 null。</summary>
    private string? ProjectAnchor()
    {
        if (_directories is null || string.IsNullOrWhiteSpace(_directories.ProjectSeeingDirectory))
            return null;

        var seeing = _directories.ProjectSeeingDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(seeing);
        return string.IsNullOrEmpty(parent) ? seeing : parent;
    }

    /// <summary>按根路径归属分类来源：项目根之下 → 项目文件；其余 → 用户文件。</summary>
    private static SkillSource ClassifySource(string root, string? projectAnchor)
    {
        if (projectAnchor is null)
            return SkillSource.UserFile;

        return IsUnder(root, projectAnchor) ? SkillSource.ProjectFile : SkillSource.UserFile;
    }

    private static bool IsUnder(string path, string anchor)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedAnchor = Path.GetFullPath(anchor)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedPath.StartsWith(
                normalizedAnchor + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 路径非法按非项目根处理（分类失效不应让装配崩溃）
            return false;
        }
    }

    // ===== 指纹 =====

    /// <summary>
    /// 廉价指纹：所有 SKILL.md 与两个 <c>skill-state.json</c> 的「路径 + 长度 + 修改时间」排序后哈希。
    /// <para>只做元数据枚举，不读文件内容；文件不变则下一次装配无需重建。</para>
    /// </summary>
    private async Task<string> ComputeFingerprintAsync(
        IReadOnlyList<SkillRoot> roots,
        CancellationToken ct)
    {
        var entries = new List<string>();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root.Directory) || !Directory.Exists(root.Directory))
                continue;

            try
            {
                var files = await Task.Run(
                    () => Directory.GetFiles(root.Directory, SkillFileScanner.SkillFileName, SearchOption.AllDirectories),
                    ct).ConfigureAwait(false);

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if (!info.Exists)
                        continue;

                    entries.Add($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 指纹失败退化为「每次都重建」：不得阻断装配
                _logger.LogWarning(ex, "计算技能根指纹失败，已跳过该根：{Directory}", root.Directory);
            }
        }

        foreach (var statePath in StateFilePaths())
        {
            var info = new FileInfo(statePath);
            entries.Add(info.Exists
                ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
                : $"{info.FullName}|missing");
        }

        entries.Sort(StringComparer.Ordinal);

        var payload = string.Join('\n', entries);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    // ===== 禁用状态 =====

    private IEnumerable<string> StateFilePaths()
    {
        if (_directories is null)
            yield break;

        yield return Path.Combine(_directories.UserSeeingDirectory, SkillStateFileName);
        yield return Path.Combine(_directories.ProjectSeeingDirectory, SkillStateFileName);
    }

    /// <summary>
    /// 读取两级 <c>skill-state.json</c>：名称取并集；上报层级项目级优先（用户级被覆盖）。
    /// </summary>
    private async Task<(IReadOnlySet<string> Names, IReadOnlyDictionary<string, ConfigLevel> Scopes)>
        LoadDisabledStateAsync(CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scopes = new Dictionary<string, ConfigLevel>(StringComparer.OrdinalIgnoreCase);

        if (_directories is null)
            return (names, scopes);

        var userDisabled = await LoadDisabledSetAsync(
            Path.Combine(_directories.UserSeeingDirectory, SkillStateFileName),
            ct).ConfigureAwait(false);
        var projectDisabled = await LoadDisabledSetAsync(
            Path.Combine(_directories.ProjectSeeingDirectory, SkillStateFileName),
            ct).ConfigureAwait(false);

        foreach (var name in userDisabled)
        {
            names.Add(name);
            scopes[name] = ConfigLevel.User;
        }

        // 项目级覆盖用户级：同名禁用项上报为项目级
        foreach (var name in projectDisabled)
        {
            names.Add(name);
            scopes[name] = ConfigLevel.Project;
        }

        return (names, scopes);
    }

    private async Task<HashSet<string>> LoadDisabledSetAsync(string filePath, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath))
            return result;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<DisabledSkillsData>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (var name in data?.DisabledSkills ?? [])
            {
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(name);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 状态文件损坏 / 不可读：按「无禁用项」处理，但必须可观测
            _logger.LogWarning(ex, "读取技能禁用状态失败，按空集合处理：{Path}", filePath);
        }

        return result;
    }

    /// <summary>禁用状态落盘模型（沿用既有格式）。</summary>
    private sealed class DisabledSkillsData
    {
        public List<string>? DisabledSkills { get; set; }
    }

    /// <summary>退订句柄。</summary>
    private sealed class Subscription(SkillCatalogCore owner, Action<SkillCatalogSnapshot> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            lock (owner._subscriberLock)
                owner._subscribers.Remove(handler);
        }
    }
}
