using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Channels;

namespace Seeing.Agent.Comms.Directories;

/// <summary>
/// <c>~/.seeing/comms/directory.json</c> 目录存储。构造时载入；每次写入 temp + move。
/// </summary>
public sealed class JsonChannelDirectoryStore : IChannelDirectoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IOptions<CommsOptions> _options;
    private readonly ILogger<JsonChannelDirectoryStore> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ChannelUserEntry> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConversationEntry> _conversations = new(StringComparer.Ordinal);

    public JsonChannelDirectoryStore(IOptions<CommsOptions> options, ILogger<JsonChannelDirectoryStore>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<JsonChannelDirectoryStore>.Instance;
        LoadFromDisk();
    }

    public static string ResolveDirectoryPath(CommsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DirectoryFilePath))
            return options.DirectoryFilePath;

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
            IReadOnlyList<ConversationEntry> page = Page(
                _conversations.Values
                    .Where(c => c.Conversation.ChannelId == channelId)
                    .OrderBy(c => c.Conversation.ExternalId, StringComparer.Ordinal),
                skip,
                take);
            return Task.FromResult(page);
        }
    }

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
        var path = ResolveDirectoryPath(_options.Value);
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

            lock (_gate)
            {
                _users.Clear();
                _conversations.Clear();
                foreach (var user in document.Users)
                    _users[ChannelKeys.User(user.ChannelId, user.ExternalId)] = user;
                foreach (var conversation in document.Conversations)
                    _conversations[ChannelKeys.Conversation(conversation.Conversation)] = conversation;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "渠道目录载入失败，按空目录启动: {Path}", path);
        }
    }

    private void PersistLocked()
    {
        var path = ResolveDirectoryPath(_options.Value);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var document = new DirectoryDocument
        {
            Users = _users.Values.OrderBy(u => u.ChannelId, StringComparer.Ordinal)
                .ThenBy(u => u.ExternalId, StringComparer.Ordinal)
                .ToList(),
            Conversations = _conversations.Values
                .OrderBy(c => c.Conversation.ChannelId, StringComparer.Ordinal)
                .ThenBy(c => c.Conversation.ExternalId, StringComparer.Ordinal)
                .ToList(),
        };

        var tmpPath = path + ".tmp";
        var payload = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(tmpPath, payload);
        File.Move(tmpPath, path, overwrite: true);
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
    }
}
