using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Configuration;
using Seeing.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Gateway.WeCom;

/// <summary>
/// 跟踪企微 conversationKey → 当前 sessionId，支持空闲超时轮换。
/// </summary>
public sealed class WeComSessionTracker
{
    private readonly object _lock = new();
    private readonly WeComOptions _options;
    private readonly ILogger<WeComSessionTracker> _logger;
    private readonly IWorkspaceProvider _workspace;
    private readonly Dictionary<string, WeComSessionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = SeeingJson.Create(indented: true, o =>
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    public WeComSessionTracker(
        IOptions<WeComOptions> options,
        IWorkspaceProvider workspace,
        ILogger<WeComSessionTracker> logger)
    {
        _options = options.Value;
        _logger = logger;
        _workspace = workspace;
        LoadFromDisk();
    }

    /// <summary>
    /// 当前状态文件路径。
    /// <para>
    /// <b>每次读取时现算</b>，不在构造期快照：默认路径落在项目的 <c>.seeing</c> 下，
    /// 构造期快照会让切换工作区后仍读写旧工作区的文件（只能靠重启子进程兜底）。
    /// 显式配置的 <c>SessionStateFile</c> 不受工作区影响。
    /// </para>
    /// </summary>
    private string StateFilePath => ResolveStateFilePath(_options.SessionStateFile);

    // 工作区切换：本 tracker 运行在 ChannelHost 子进程内，切工作区时由 GatewayWorkspaceParticipant
    // 停掉并按新根重启，路径天然重算；同进程宿主下不提供对齐入口（避免留下死代码）。

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
            entry.LastActiveAtUtc = DateTime.Now;
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
            LastActiveAtUtc = DateTime.Now
        };
        _entries[conversationKey] = existing;
        PersistLocked();
        return existing;
    }

    private bool ShouldRotateForIdle(WeComSessionEntry entry)
    {
        if (_options.SessionIdleTimeoutMinutes <= 0)
            return false;

        var idle = DateTime.Now - entry.LastActiveAtUtc;
        return idle >= TimeSpan.FromMinutes(_options.SessionIdleTimeoutMinutes);
    }

    private WeComSessionEntry RotateEntry(string conversationKey, WeComSessionEntry entry, string reason)
    {
        var newSessionId = WeComSessionResolver.GenerateRotatedSessionId(conversationKey);
        entry.CurrentSessionId = newSessionId;
        entry.LastActiveAtUtc = DateTime.Now;
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
        var stateFilePath = StateFilePath;
        try
        {
            if (!File.Exists(stateFilePath))
                return;

            var json = File.ReadAllText(stateFilePath);
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
            _logger.LogWarning(ex, "加载 WeCom 会话状态失败: {Path}", stateFilePath);
        }
    }

    private void PersistLocked()
    {
        var stateFilePath = StateFilePath;
        try
        {
            var directory = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var state = new WeComSessionStateFile
            {
                Entries = _entries.Values.ToList()
            };

            File.WriteAllText(stateFilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存 WeCom 会话状态失败: {Path}", stateFilePath);
        }
    }

    private string ResolveStateFilePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        return Path.Combine(
            _workspace.ProjectSeeingDirectory,
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
