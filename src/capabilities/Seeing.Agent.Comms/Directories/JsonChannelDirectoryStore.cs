using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Files;
using Seeing.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Comms.Directories;

/// <summary>
/// 渠道目录存储（默认 <c>{项目}/.seeing/comms/directory.json</c>，工作区未选定时回退
/// <c>~/.seeing/comms/directory.json</c>）。构造时载入；读取前按文件时间戳增量同步，
/// 写入前先把磁盘上的外部条目合并进来。
/// </summary>
public sealed class JsonChannelDirectoryStore : IChannelDirectoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = SeeingJson.Create(indented: true, o =>
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Converters.Add(new JsonStringEnumConverter());
    });

    private readonly IOptions<CommsOptions> _options;
    private readonly ILogger<JsonChannelDirectoryStore> _logger;
    private readonly ISeeingDirectories? _directories;
    private readonly object _gate = new();
    private readonly Dictionary<string, ChannelUserEntry> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationEntry> _conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockedConversation> _blocked = new(StringComparer.Ordinal);

    /// <summary>
    /// 上次与磁盘同步时的文件时间戳。用于判断内存快照是否已落后于磁盘。
    /// <para><b>为什么需要它</b>：本文件是<b>多进程共享</b>的（WebUI 主进程 / GatewayServer /
    /// ChannelHost 桥子进程）。只在构造时读一次盘会让「后写入方的条目」永远不出现在本进程内存里
    /// ——表现为「定时任务能选到出站会话，目录页却是空的」。</para>
    /// </summary>
    private DateTime _syncedWriteUtc = DateTime.MinValue;

    public JsonChannelDirectoryStore(
        IOptions<CommsOptions> options,
        ILogger<JsonChannelDirectoryStore>? logger = null,
        ISeeingDirectories? directories = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<JsonChannelDirectoryStore>.Instance;
        _directories = directories;
        LoadFromDisk();
    }

    /// <summary>
    /// 解析目录文件路径。
    /// <para><b>默认落在项目级</b>：<c>{项目}/.seeing/comms/directory.json</c>——
    /// 目录是「这个工作区收到过哪些渠道对话」的事实，随工作区走才不会串场
    /// （同一台机器上跑多个项目时，用户级单一路径会让不同项目的对话混在一起）。</para>
    /// <para>工作区未选定（占位目录）时回退用户级 <c>~/.seeing/comms/directory.json</c>：
    /// 此时没有项目可归属，但桥进程仍可能在运行并需要落盘。</para>
    /// </summary>
    public static string ResolveDirectoryPath(CommsOptions options, ISeeingDirectories? directories = null)
    {
        if (!string.IsNullOrWhiteSpace(options.DirectoryFilePath))
            return options.DirectoryFilePath;

        // ProjectSeeingDirectory 在工作区未选定时<b>会抛异常</b>（契约如此），
        // 这里不能让它冒泡到构造函数——否则 IChannelDirectory 整个服务都解析不出来。
        string? projectSeeingDirectory = null;
        try
        {
            projectSeeingDirectory = directories?.ProjectSeeingDirectory;
        }
        catch (Exception)
        {
            // 未选定工作区 / 提供者尚未就绪：走下面的用户级回退。
        }

        if (!string.IsNullOrWhiteSpace(projectSeeingDirectory)
            && !WorkspaceProviderPlaceholder.IsPlaceholder(projectSeeingDirectory))
        {
            return Path.Combine(projectSeeingDirectory, "comms", "directory.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".seeing", "comms", "directory.json");
    }

    public Task SaveUserAsync(ChannelUserEntry user, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _users[ChannelKeys.User(user.ChannelId, user.ExternalId)] = user;
            PersistLocked();
        }

        return Task.CompletedTask;
    }

    public Task SaveConversationAsync(ConversationEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _conversations[ChannelKeys.Conversation(entry.Conversation)] = entry;
            PersistLocked();
        }

        return Task.CompletedTask;
    }

    public Task<ChannelUserEntry?> FindUserAsync(string channelId, string externalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            SyncFromDiskIfNewerLocked();
            _users.TryGetValue(ChannelKeys.User(channelId, externalId), out var user);
            return Task.FromResult(user);
        }
    }

    public Task<ConversationEntry?> FindConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = ChannelKeys.Conversation(new ConversationInfo
        {
            ChannelId = channelId,
            Kind = kind,
            ExternalId = externalId,
        });
        lock (_gate)
        {
            SyncFromDiskIfNewerLocked();
            _conversations.TryGetValue(key, out var entry);
            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<ChannelUserEntry>> ListUsersAsync(
        string channelId, int skip, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            SyncFromDiskIfNewerLocked();
            IReadOnlyList<ChannelUserEntry> page = Page(
                _users.Values.Where(u => u.ChannelId == channelId).OrderBy(u => u.ExternalId, StringComparer.Ordinal),
                skip,
                take);
            return Task.FromResult(page);
        }
    }

    public Task<IReadOnlyList<ConversationEntry>> ListConversationsAsync(
        string channelId, int skip, int take, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            SyncFromDiskIfNewerLocked();
            IReadOnlyList<ConversationEntry> page = Page(
                _conversations.Values
                    .Where(c => c.Conversation.ChannelId == channelId)
                    .OrderBy(c => c.Conversation.ExternalId, StringComparer.Ordinal),
                skip,
                take);
            return Task.FromResult(page);
        }
    }

    public Task<IReadOnlyList<string>> ListChannelIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            SyncFromDiskIfNewerLocked();

            // 屏蔽名单里的通道也算「有数据的通道」：否则某通道的对话全被屏蔽后，
            // 它就从列表里消失，用户再也进不到该通道去解封。
            IReadOnlyList<string> channelIds = _conversations.Values
                .Select(c => c.Conversation.ChannelId)
                .Concat(_blocked.Values.Select(b => b.ChannelId))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(channelIds);
        }
    }

    public Task DeleteUserAsync(string channelId, string externalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_users.Remove(ChannelKeys.User(channelId, externalId)))
                PersistLocked();
        }

        return Task.CompletedTask;
    }

    public Task DeleteConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_conversations.Remove(ChannelKeys.Conversation(new ConversationInfo
                {
                    ChannelId = channelId,
                    Kind = kind,
                    ExternalId = externalId
                })))
            {
                PersistLocked();
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BlockedConversation>> ListBlockedConversationsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            SyncFromDiskIfNewerLocked();
            IReadOnlyList<BlockedConversation> list = _blocked.Values
                .OrderBy(b => b.ChannelId, StringComparer.Ordinal)
                .ThenBy(b => b.ExternalId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task SaveBlockedConversationAsync(BlockedConversation entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _blocked[BlockedKey(entry.ChannelId, entry.Kind, entry.ExternalId)] = entry;
            PersistLocked();
        }

        return Task.CompletedTask;
    }

    public Task DeleteBlockedConversationAsync(
        string channelId, ConversationKind kind, string externalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_blocked.Remove(BlockedKey(channelId, kind, externalId)))
                PersistLocked();
        }

        return Task.CompletedTask;
    }

    public bool IsBlocked(string channelId, ConversationKind kind, string externalId)
    {
        lock (_gate)
        {
            SyncFromDiskIfNewerLocked();
            return _blocked.ContainsKey(BlockedKey(channelId, kind, externalId));
        }
    }

    /// <summary>屏蔽名单键：与目录键同一套规则（渠道 + 类型 + 外部 id）。</summary>
    internal static string BlockedKey(string channelId, ConversationKind kind, string externalId)
        => ChannelKeys.Conversation(channelId, kind, externalId);

    public Task LoadIntoAsync(Action<ChannelUserEntry, ConversationEntry?> sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        List<ChannelUserEntry> users;
        List<ConversationEntry> conversations;
        lock (_gate)
        {
            users = _users.Values.ToList();
            conversations = _conversations.Values.ToList();
        }

        foreach (var user in users)
            sink(user, null);

        foreach (var conversation in conversations)
        {
            var anchor = users.FirstOrDefault(u => u.ChannelId == conversation.Conversation.ChannelId);
            if (anchor is not null)
                sink(anchor, conversation);
        }

        return Task.CompletedTask;
    }

    private void LoadFromDisk()
    {
        var path = ResolveDirectoryPath(_options.Value, _directories);
        if (!File.Exists(path))
        {
            _syncedWriteUtc = DateTime.MinValue;
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var document = JsonSerializer.Deserialize<DirectoryDocument>(json, JsonOptions);
            if (document is null)
                return;

            lock (_gate)
            {
                _users.Clear();
                _conversations.Clear();
                _blocked.Clear();
                foreach (var user in document.Users)
                    _users[ChannelKeys.User(user.ChannelId, user.ExternalId)] = user;
                foreach (var conversation in document.Conversations)
                    _conversations[ChannelKeys.Conversation(conversation.Conversation)] = conversation;
                foreach (var blocked in document.Blocked)
                    _blocked[BlockedKey(blocked.ChannelId, blocked.Kind, blocked.ExternalId)] = blocked;
            }

            _syncedWriteUtc = ReadWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "渠道目录载入失败，按空目录启动: {Path}", path);
        }
    }

    /// <summary>
    /// 读取文件时间戳；文件不存在或不可读返回 <see cref="DateTime.MinValue"/>。
    /// </summary>
    private static DateTime ReadWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// 磁盘比内存新时重新载入（调用方须持有 <c>_gate</c>）。
    /// <para>每次读/写前都过一遍：文件由多进程共享，任一方写入后其它进程必须能看见。</para>
    /// <para>只做「变新才重载」而不是无条件重读：同进程内的连续读写不会反复解析 JSON。</para>
    /// </summary>
    private void SyncFromDiskIfNewerLocked()
    {
        var path = ResolveDirectoryPath(_options.Value, _directories);
        var diskWriteUtc = ReadWriteTimeUtc(path);

        // 文件不存在（或读不到时间戳）时不重载：内存里的条目仍应可读，
        // 下次写入会把它们重新落盘。
        if (diskWriteUtc == DateTime.MinValue || diskWriteUtc <= _syncedWriteUtc)
            return;

        LoadFromDiskLocked(path);
    }

    /// <summary>重载指定文件到内存（调用方须持有 <c>_gate</c>；失败只告警，保留现有内存）。</summary>
    private void LoadFromDiskLocked(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var document = JsonSerializer.Deserialize<DirectoryDocument>(json, JsonOptions);
            if (document is null)
                return;

            _users.Clear();
            _conversations.Clear();
            _blocked.Clear();
            foreach (var user in document.Users)
                _users[ChannelKeys.User(user.ChannelId, user.ExternalId)] = user;
            foreach (var conversation in document.Conversations)
                _conversations[ChannelKeys.Conversation(conversation.Conversation)] = conversation;
            foreach (var blocked in document.Blocked)
                _blocked[BlockedKey(blocked.ChannelId, blocked.Kind, blocked.ExternalId)] = blocked;

            _syncedWriteUtc = ReadWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 读失败时保留当前内存：宁可短暂陈旧，也不要因为一次读失败把目录清空。
            _logger?.LogWarning(ex, "渠道目录增量重载失败，沿用内存快照: {Path}", path);
        }
    }

    /// <summary>
    /// 把内存快照落盘（调用方须持有 <c>_gate</c>）。
    /// <para><b>写前先合并磁盘</b>：本文件由多进程共享，若直接覆盖会丢掉其它进程刚写入的条目。
    /// 合并方向是「磁盘有、内存没有」的记录补进来；内存里已有的以内存为准（它含本次修改）。</para>
    /// </summary>
    private void PersistLocked()
    {
        var path = ResolveDirectoryPath(_options.Value, _directories);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        MergeFromDiskLocked(path);

        var document = new DirectoryDocument
        {
            Users = _users.Values.OrderBy(u => u.ChannelId, StringComparer.Ordinal)
                .ThenBy(u => u.ExternalId, StringComparer.Ordinal)
                .ToList(),
            Conversations = _conversations.Values
                .OrderBy(c => c.Conversation.ChannelId, StringComparer.Ordinal)
                .ThenBy(c => c.Conversation.ExternalId, StringComparer.Ordinal)
                .ToList(),
            Blocked = _blocked.Values
                .OrderBy(b => b.ChannelId, StringComparer.Ordinal)
                .ThenBy(b => b.ExternalId, StringComparer.Ordinal)
                .ToList(),
        };

        var payload = JsonSerializer.Serialize(document, JsonOptions);
        AtomicFileWrite.WriteAllTextAtomic(path, payload, subject: "channel-directory", _logger);
        _syncedWriteUtc = ReadWriteTimeUtc(path);
    }

    /// <summary>
    /// 把磁盘上「内存里还没有」的条目补进内存（调用方须持有 <c>_gate</c>）。
    /// <para>只补缺失键、不覆盖已有键：内存是本次修改的结果，磁盘可能是别的进程较早的快照。</para>
    /// </summary>
    private void MergeFromDiskLocked(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var document = JsonSerializer.Deserialize<DirectoryDocument>(json, JsonOptions);
            if (document is null)
                return;

            foreach (var user in document.Users)
                _users.TryAdd(ChannelKeys.User(user.ChannelId, user.ExternalId), user);

            foreach (var conversation in document.Conversations)
                _conversations.TryAdd(ChannelKeys.Conversation(conversation.Conversation), conversation);

            foreach (var blocked in document.Blocked)
                _blocked.TryAdd(BlockedKey(blocked.ChannelId, blocked.Kind, blocked.ExternalId), blocked);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // 磁盘内容坏了不该阻断写入：本次快照仍要落盘（否则本进程的修改永远保存不了）。
            _logger?.LogWarning(ex, "渠道目录写前合并失败，按内存快照覆盖: {Path}", path);
        }
    }

    private static IReadOnlyList<T> Page<T>(IEnumerable<T> source, int skip, int take)
    {
        if (take <= 0)
            return [];
        return source.Skip(Math.Max(0, skip)).Take(take).ToList();
    }

    private sealed class DirectoryDocument
    {
        public List<ChannelUserEntry> Users { get; set; } = [];
        public List<ConversationEntry> Conversations { get; set; } = [];

        /// <summary>屏蔽名单。旧文件没有这个键 → 反序列化为空列表，向前兼容。</summary>
        public List<BlockedConversation> Blocked { get; set; } = [];
    }
}
