using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Mcp.Skills;

/// <summary>
/// MCP Skills 扩展 → <see cref="ISkillContributionSource"/>。
/// 发现期只列元数据；加载期 <c>skills/get</c> + <c>resources/read</c>（经可注入的 <c>mcp.resource.read</c> 授权）。
/// 禁止调用 <see cref="ISkillCatalog.RegisterAsync"/>。
/// </summary>
public sealed class McpSkillContributionSource : ISkillContributionSource
{
    private readonly IMcpManager _manager;
    private readonly ISkillContributionRegistry? _registry;
    private readonly Func<string, string, CancellationToken, Task<bool>>? _authorizeResourceRead;
    private readonly ILogger<McpSkillContributionSource>? _logger;
    private readonly ConcurrentDictionary<string, SkillIndexEntry> _index =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _attachGate = new();
    private bool _attached;

    /// <param name="manager">取已连接 Session / 状态。</param>
    /// <param name="registry">可选；用于 StatusChanged 时触发目录重拉。</param>
    /// <param name="authorizeResourceRead">
    /// <c>(server, uri, ct) => allowed</c>；缺失则 Fail-closed（不读 resource）。
    /// </param>
    public McpSkillContributionSource(
        IMcpManager manager,
        ISkillContributionRegistry? registry = null,
        Func<string, string, CancellationToken, Task<bool>>? authorizeResourceRead = null,
        ILogger<McpSkillContributionSource>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _registry = registry;
        _authorizeResourceRead = authorizeResourceRead;
        _logger = logger;
    }

    /// <inheritdoc />
    public string SourceKind => "mcp";

    /// <summary>
    /// 订阅 Manager 状态变更；断开 / 禁用后 <see cref="ListAsync"/> 自然变空，并 <see cref="ISkillContributionRegistry.NotifyChanged"/>。
    /// </summary>
    public void Attach()
    {
        lock (_attachGate)
        {
            if (_attached)
                return;
            _manager.StatusChanged += OnStatusChanged;
            _attached = true;
        }
    }

    /// <summary>取消状态订阅（Deactivate 时调用）。</summary>
    public void Detach()
    {
        lock (_attachGate)
        {
            if (!_attached)
                return;
            _manager.StatusChanged -= OnStatusChanged;
            _attached = false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SkillContributionDescriptor>> ListAsync(
        CancellationToken ct = default)
    {
        var nextIndex = new Dictionary<string, SkillIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<SkillContributionDescriptor>();

        foreach (var (serverName, status) in _manager.GetAllStatus())
        {
            ct.ThrowIfCancellationRequested();

            if (!status.IsAvailable)
                continue;
            if (status.Capabilities is not { SkillsExtension: true })
                continue;

            var session = _manager.TryGetSession(serverName);
            if (session is null)
                continue;

            McpSkillExtListResult listed;
            try
            {
                listed = await session.ListSkillsAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "MCP Server {Server} skills/list 失败，已跳过该 server 技能贡献", serverName);
                continue;
            }

            if (listed.NotSupported || !string.IsNullOrEmpty(listed.ErrorMessage))
            {
                if (!listed.NotSupported)
                {
                    _logger?.LogWarning(
                        "MCP Server {Server} skills/list 失败：{Error}",
                        serverName,
                        listed.ErrorMessage);
                }

                continue;
            }

            foreach (var skill in listed.Items)
            {
                if (string.IsNullOrWhiteSpace(skill.Name) || string.IsNullOrWhiteSpace(skill.Uri))
                    continue;

                var key = BuildKey(serverName, skill.Name);
                if (nextIndex.ContainsKey(key))
                {
                    _logger?.LogWarning(
                        "MCP 技能键冲突，已跳过重复项：Key={Key} Server={Server} Name={Name}",
                        key,
                        serverName,
                        skill.Name);
                    continue;
                }

                nextIndex[key] = new SkillIndexEntry(serverName, skill.Name, skill.Uri);
                descriptors.Add(new SkillContributionDescriptor(
                    key,
                    skill.Description ?? string.Empty,
                    ProviderId: $"mcp:{serverName}"));
            }
        }

        _index.Clear();
        foreach (var (key, entry) in nextIndex)
            _index[key] = entry;

        return descriptors;
    }

    /// <inheritdoc />
    public async Task<SkillContributionContent?> LoadAsync(
        string key,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (!_index.TryGetValue(key, out var entry))
            return null;

        var session = _manager.TryGetSession(entry.ServerName);
        if (session is null)
            return null;

        McpSkillExtGetResult got;
        try
        {
            got = await session.GetSkillAsync(entry.Uri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "MCP Server {Server} skills/get 失败：Uri={Uri}",
                entry.ServerName,
                entry.Uri);
            return null;
        }

        if (got.NotSupported || !string.IsNullOrEmpty(got.ErrorMessage))
        {
            _logger?.LogWarning(
                "MCP Server {Server} skills/get 不可用：{Error}",
                entry.ServerName,
                got.ErrorMessage ?? "not supported");
            return null;
        }

        var skillUri = string.IsNullOrWhiteSpace(got.Uri) ? entry.Uri : got.Uri!;
        var body = await ReadAuthorizedTextAsync(session, entry.ServerName, skillUri, ct)
            .ConfigureAwait(false);
        if (body is null)
        {
            // get 内联正文可作为回退，但仍须过 resource 门（对 skillUri 授权）
            if (!string.IsNullOrWhiteSpace(got.Content))
            {
                if (!await AuthorizeAsync(entry.ServerName, skillUri, ct).ConfigureAwait(false))
                    return null;
                body = got.Content;
            }
            else
            {
                return null;
            }
        }

        var attachments = new List<SkillContributionAttachment>();
        if (got.AttachmentUris is { Count: > 0 })
        {
            foreach (var attachmentUri in got.AttachmentUris)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(attachmentUri))
                    continue;

                var text = await ReadAuthorizedTextAsync(session, entry.ServerName, attachmentUri, ct)
                    .ConfigureAwait(false);
                if (text is null)
                    continue;

                attachments.Add(new SkillContributionAttachment(
                    RelativePath: DeriveRelativePath(skillUri, attachmentUri),
                    Uri: attachmentUri,
                    MimeType: null));
            }
        }

        return new SkillContributionContent(body, attachments);
    }

    /// <summary>锁定键格式：只保留 <c>[A-Za-z0-9_-]</c>，其余变 <c>_</c>。</summary>
    public static string SanitizeToken(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var buffer = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')
                buffer.Append(ch);
            else
                buffer.Append('_');
        }

        return buffer.ToString();
    }

    /// <summary>目录键：<c>mcp_{server}_{skill}</c>（已净化）。</summary>
    public static string BuildKey(string serverId, string skillName)
        => $"mcp_{SanitizeToken(serverId)}_{SanitizeToken(skillName)}";

    private void OnStatusChanged(object? sender, McpStatusChangedEventArgs e)
        => _registry?.NotifyChanged();

    private async Task<bool> AuthorizeAsync(string serverName, string uri, CancellationToken ct)
    {
        if (_authorizeResourceRead is null)
            return false;

        try
        {
            return await _authorizeResourceRead(serverName, uri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "MCP 技能 resource 授权回调失败，已拒绝：Server={Server} Uri={Uri}",
                serverName,
                uri);
            return false;
        }
    }

    private async Task<string?> ReadAuthorizedTextAsync(
        IMcpSession session,
        string serverName,
        string uri,
        CancellationToken ct)
    {
        if (!await AuthorizeAsync(serverName, uri, ct).ConfigureAwait(false))
            return null;

        McpResourceReadResult read;
        try
        {
            read = await session.ReadResourceAsync(uri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "MCP Server {Server} resources/read 失败：Uri={Uri}",
                serverName,
                uri);
            return null;
        }

        if (read.NotSupported || !string.IsNullOrEmpty(read.ErrorMessage) || read.Contents.Count == 0)
            return null;

        var texts = read.Contents
            .Select(c => c.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();
        return texts.Length == 0 ? null : string.Join("\n", texts);
    }

    private static string DeriveRelativePath(string skillUri, string attachmentUri)
    {
        // 同 scheme+authority 时：优先相对技能「目录」；否则用 attachment 的 AbsolutePath（去首 /）
        try
        {
            if (Uri.TryCreate(attachmentUri, UriKind.Absolute, out var attach))
            {
                if (Uri.TryCreate(skillUri, UriKind.Absolute, out var skill)
                    && string.Equals(skill.Scheme, attach.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(skill.Authority, attach.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    var skillDir = skill.AbsolutePath.TrimEnd('/');
                    var lastSlash = skillDir.LastIndexOf('/');
                    if (lastSlash >= 0)
                        skillDir = skillDir[..lastSlash];

                    var attachPath = attach.AbsolutePath;
                    if (!string.IsNullOrEmpty(skillDir)
                        && attachPath.StartsWith(skillDir, StringComparison.OrdinalIgnoreCase)
                        && attachPath.Length > skillDir.Length
                        && attachPath[skillDir.Length] == '/')
                    {
                        return attachPath[(skillDir.Length + 1)..].TrimStart('/');
                    }
                }

                return attach.AbsolutePath.TrimStart('/');
            }
        }
        catch
        {
            // 忽略 URI 解析异常，走兜底
        }

        var slash = attachmentUri.LastIndexOf('/');
        return slash >= 0 && slash < attachmentUri.Length - 1
            ? attachmentUri[(slash + 1)..]
            : attachmentUri;
    }

    private readonly record struct SkillIndexEntry(string ServerName, string SkillName, string Uri);
}
