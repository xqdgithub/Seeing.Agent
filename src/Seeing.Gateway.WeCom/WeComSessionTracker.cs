using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 跟踪企微 conversationKey → 当前 sessionId，支持空闲超时轮换。
/// </summary>
public sealed class WeComSessionTracker
{
    private readonly object _lock = new();
    private readonly WeComOptions _options;
    private readonly ILogger<WeComSessionTracker> _logger;
    private readonly string _stateFilePath;
    private readonly Dictionary<string, WeComSessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WeComSessionTracker(IOptions<WeComOptions> options, ILogger<WeComSessionTracker> logger)
    {
        _options = options.Value;
        _logger = logger;
        _stateFilePath = ResolveStateFilePath(_options.SessionStateFile);
        LoadFromDisk();
    }

    /// <summary>解析当前应使用的 sessionId（必要时因 idle 自动轮换）</summary>
    public string ResolveSessionId(ParsedWeComMessage message)
    {
        var conversationKey = WeComSessionResolver.ResolveConversationKey(message, _options);
        lock (_lock)
        {
            var entry = GetOrCreateEntry(conversationKey);
            if (ShouldRotateForIdle(entry))
                entry = RotateEntry(conversationKey, entry, reason: "idle");

            return entry.CurrentSessionId;
        }
    }

    /// <summary>消息处理完成后刷新活跃时间</summary>
    public void Touch(ParsedWeComMessage message)
    {
        var conversationKey = WeComSessionResolver.ResolveConversationKey(message, _options);
        lock (_lock)
        {
            var entry = GetOrCreateEntry(conversationKey);
            entry.LastActiveAtUtc = DateTime.UtcNow;
            PersistLocked();
        }
    }

    public bool IsIdle(ParsedWeComEnterChat enterChat)
    {
        var conversationKey = WeComSessionResolver.ResolveConversationKey(enterChat, _options);
        lock (_lock)
        {
            if (!_entries.TryGetValue(conversationKey, out var entry))
                return false;

            return ShouldRotateForIdle(entry);
        }
    }

    public string RotateSession(ParsedWeComMessage message, string reason = "manual")
    {
        var conversationKey = WeComSessionResolver.ResolveConversationKey(message, _options);
        lock (_lock)
        {
            var entry = GetOrCreateEntry(conversationKey);
            entry = RotateEntry(conversationKey, entry, reason);
            return entry.CurrentSessionId;
        }
    }

    public string RotateSession(ParsedWeComEnterChat enterChat, string reason = "enter_chat")
    {
        var conversationKey = WeComSessionResolver.ResolveConversationKey(enterChat, _options);
        lock (_lock)
        {
            var entry = GetOrCreateEntry(conversationKey);
            entry = RotateEntry(conversationKey, entry, reason);
            return entry.CurrentSessionId;
        }
    }

    private WeComSessionEntry GetOrCreateEntry(string conversationKey)
    {
        if (_entries.TryGetValue(conversationKey, out var existing))
            return existing;

        existing = new WeComSessionEntry
        {
            ConversationKey = conversationKey,
            CurrentSessionId = conversationKey,
            LastActiveAtUtc = DateTime.UtcNow
        };
        _entries[conversationKey] = existing;
        PersistLocked();
        return existing;
    }

    private bool ShouldRotateForIdle(WeComSessionEntry entry)
    {
        if (_options.SessionIdleTimeoutMinutes <= 0)
            return false;

        var idle = DateTime.UtcNow - entry.LastActiveAtUtc;
        return idle >= TimeSpan.FromMinutes(_options.SessionIdleTimeoutMinutes);
    }

    private WeComSessionEntry RotateEntry(string conversationKey, WeComSessionEntry entry, string reason)
    {
        var newSessionId = WeComSessionResolver.GenerateRotatedSessionId(conversationKey);
        entry.CurrentSessionId = newSessionId;
        entry.LastActiveAtUtc = DateTime.UtcNow;
        PersistLocked();
        _logger.LogInformation(
            "WeCom 会话轮换: Key={ConversationKey}, SessionId={SessionId}, Reason={Reason}",
            conversationKey,
            newSessionId,
            reason);
        return entry;
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return;

            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<WeComSessionStateFile>(json, JsonOptions);
            if (state?.Entries == null)
                return;

            lock (_lock)
            {
                _entries.Clear();
                foreach (var entry in state.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.ConversationKey))
                        continue;

                    _entries[entry.ConversationKey] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载 WeCom 会话状态失败: {Path}", _stateFilePath);
        }
    }

    private void PersistLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var state = new WeComSessionStateFile
            {
                Entries = _entries.Values.ToList()
            };

            File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存 WeCom 会话状态失败: {Path}", _stateFilePath);
        }
    }

    private static string ResolveStateFilePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        return Path.Combine(
            Directory.GetCurrentDirectory(),
            ".seeing",
            "gateway-clients",
            "wecom.sessions.json");
    }

    internal IReadOnlyDictionary<string, WeComSessionEntry> GetEntriesForTesting()
    {
        lock (_lock)
        {
            return new Dictionary<string, WeComSessionEntry>(_entries, StringComparer.OrdinalIgnoreCase);
        }
    }
}

internal sealed class WeComSessionStateFile
{
    public List<WeComSessionEntry> Entries { get; set; } = [];
}

public sealed class WeComSessionEntry
{
    public required string ConversationKey { get; set; }

    public required string CurrentSessionId { get; set; }

    public DateTime LastActiveAtUtc { get; set; }
}
