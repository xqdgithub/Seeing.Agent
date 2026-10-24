using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Instructions;

namespace Seeing.Agent.Core.Context.Sources;

/// <summary>
/// 项目指令源（AGENTS.md 等）：<c>Target = Message</c>，初始渲染以用户消息注入，
/// 增量变化以叙述消息追加。
/// <para>读值：逐路径三态（Loaded / Missing / Unreadable），<c>Canonical</c> 只放
/// <c>{"files":[{"path","sha256","mtime"}]}</c>（排序），正文经 <c>Payload</c> 仅在本次渲染使用、不落盘。</para>
/// <para>cwd / workspaceRoot 的解析：源在 <see cref="ReadAsync"/> 时自行解析（装配器不传外部上下文）。
/// 默认取「进程当前目录 + <see cref="IWorkspaceProvider.GetProjectRoot"/>」；可通过
/// <c>Func&lt;(string Cwd, string WorkspaceRoot)&gt;</c> 接缝覆盖（Task 12 由 ExecutionRunner
/// 接入会话 cwd 时使用该接缝，避免 Core 反向依赖执行态）。</para>
/// </summary>
public sealed class ProjectInstructionsContextSource : IContextSource
{
    private const string TruncatedSuffix = "\n…（项目指令内容已按上下文基线上限截断）";

    private readonly IInstructionFileReader _reader;
    private readonly Func<(string Cwd, string WorkspaceRoot)> _rootsProvider;
    private readonly ILogger<ProjectInstructionsContextSource> _logger;

    /// <summary>
    /// 创建项目指令源（默认根解析：进程当前目录 + 工作区项目根）。
    /// </summary>
    public ProjectInstructionsContextSource(
        IInstructionFileReader reader,
        IWorkspaceProvider workspaceProvider,
        ILogger<ProjectInstructionsContextSource> logger)
        : this(
            reader,
            CreateDefaultRootsProvider(workspaceProvider),
            logger)
    {
    }

    /// <summary>
    /// 创建项目指令源并显式指定根解析接缝（执行入口接入会话 cwd 时使用）。
    /// </summary>
    public ProjectInstructionsContextSource(
        IInstructionFileReader reader,
        Func<(string Cwd, string WorkspaceRoot)> rootsProvider,
        ILogger<ProjectInstructionsContextSource> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(rootsProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _rootsProvider = rootsProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public ContextSourceKey Key => new(typeof(ProjectInstructionsContextSource).FullName!);

    /// <inheritdoc />
    public ContextSourceScope Scope => ContextSourceScope.Project;

    /// <inheritdoc />
    public int ValueSchemaVersion => 1;

    /// <inheritdoc />
    public ContextSourceTarget Target => ContextSourceTarget.Message;

    /// <inheritdoc />
    public int MaxBaselineChars => 32_000;

    /// <inheritdoc />
    public async Task<ContextSourceValue> ReadAsync(CancellationToken ct = default)
    {
        var (cwd, workspaceRoot) = _rootsProvider();

        IReadOnlyList<InstructionFileReadResult> results;
        try
        {
            results = await _reader.ReadAsync(cwd, workspaceRoot, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 端口实现违约（抛异常）：不当作缺失，按不可用保留上次叙述
            _logger.LogWarning(
                ex,
                "读取项目指令候选路径失败，按不可用处理：{Cwd} {WorkspaceRoot}",
                cwd,
                workspaceRoot);
            return Unavailable();
        }

        if (results.Count == 0)
        {
            // 无任何候选路径（如 cwd 在工作区外且无用户目录）：不参与 diff
            _logger.LogDebug("未发现任何指令候选路径：{Cwd} {WorkspaceRoot}", cwd, workspaceRoot);
            return Unavailable();
        }

        var loaded = results
            .Where(result => result.Status == InstructionFileReadStatus.Loaded && result.Content is not null)
            .ToList();

        if (loaded.Count == 0)
        {
            var unreadable = results
                .Where(result => result.Status == InstructionFileReadStatus.Unreadable)
                .ToList();

            if (unreadable.Count > 0)
            {
                // Review Focus #1：暂时不可读 → 保留上次已叙述内容，绝不叙述「已删除」
                _logger.LogWarning(
                    "项目指令文件暂时不可读，保留上次已叙述内容：{Count} 个路径（{Cwd}）",
                    unreadable.Count,
                    cwd);
                return Unavailable();
            }

            // 全部确认缺失：返回 Removed 参与 diff（从未建立基线时装配器不会叙述）
            return new ContextSourceValue(
                ContextValueState.Removed,
                null,
                new ProjectInstructionsPayload(cwd, [], []));
        }

        var files = loaded
            .Select(result => new InstructionFile
            {
                Path = result.Path,
                Content = result.Content!,
                Hash = $"sha256:{Sha256Hex(result.Content!)}",
                LastModified = TryGetLastWriteTimeUtc(result.Path)
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        var canonical = BuildCanonical(files);

        return new ContextSourceValue(
            ContextValueState.Value,
            canonical,
            new ProjectInstructionsPayload(cwd, files, []));
    }

    /// <inheritdoc />
    public string RenderInitial(ContextSourceValue value)
    {
        var payload = value.Payload as ProjectInstructionsPayload;
        IReadOnlyList<InstructionFile> files = payload?.Files ?? [];
        var cwd = payload?.Cwd ?? _rootsProvider().Cwd;

        var envelope = ProjectInstructionsRenderer.Wrap(
            cwd,
            ProjectInstructions.Reasons.Initial,
            files);

        // 截断在渲染内部完成：截断后的文本才是存库基线文本（装配器只观测、不再二次裁剪）
        return Truncate(envelope, cwd);
    }

    /// <inheritdoc />
    public string? Render(ContextSourceValue previous, ContextSourceValue current)
    {
        var previousFiles = ReadFingerprints(previous.Canonical);
        var currentFiles = ReadFingerprints(current.Canonical);

        var previousByPath = ToMap(previousFiles);
        var currentByPath = ToMap(currentFiles);

        var payload = current.Payload as ProjectInstructionsPayload;
        var contentByPath = payload?.Files.ToDictionary(file => file.Path, StringComparer.Ordinal)
            ?? new Dictionary<string, InstructionFile>(StringComparer.Ordinal);

        var updated = new List<InstructionFile>();
        foreach (var entry in currentFiles)
        {
            var isNew = !previousByPath.TryGetValue(entry.Path, out var previousEntry);
            var isChanged = !isNew &&
                            !string.Equals(previousEntry.Sha256, entry.Sha256, StringComparison.Ordinal);

            if (!isNew && !isChanged)
                continue;

            if (!contentByPath.TryGetValue(entry.Path, out var file))
            {
                // 正文缺失时不渲染半个文件块：记录后跳过该路径的叙述（本次仍会推进已叙述值）
                _logger.LogWarning(
                    "项目指令文件缺少正文，增量叙述已跳过该路径：{Path}",
                    entry.Path);
                continue;
            }

            updated.Add(file);
        }

        var removed = previousFiles
            .Where(entry => !currentByPath.ContainsKey(entry.Path))
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (updated.Count == 0 && removed.Count == 0)
            return null;

        var sb = new StringBuilder();
        if (updated.Count > 0)
        {
            sb.AppendLine("以下项目指令文件已新增或更新（以最新内容为准）：");
            sb.Append(ProjectInstructionsRenderer.RenderFileBlocks(updated));
        }

        if (removed.Count > 0)
        {
            if (updated.Count > 0)
                sb.AppendLine();

            sb.AppendLine("以下项目指令文件已不再适用，请勿再遵循（no longer applies）：");
            foreach (var path in removed)
                sb.Append("- ").AppendLine(path);
        }

        return sb.ToString().TrimEnd();
    }

    private static Func<(string Cwd, string WorkspaceRoot)> CreateDefaultRootsProvider(
        IWorkspaceProvider workspaceProvider)
    {
        ArgumentNullException.ThrowIfNull(workspaceProvider);

        // 选择注入 IWorkspaceProvider 而非读取会话状态：本任务源无外部上下文参数，
        // 而项目根只有工作区提供者能给出；执行 cwd 暂用进程当前目录，
        // Task 12 由 ExecutionRunner 以接缝注入会话 cwd。
        return () => (Directory.GetCurrentDirectory(), workspaceProvider.GetProjectRoot());
    }

    private static ContextSourceValue Unavailable() =>
        new(ContextValueState.Unavailable, null, null);

    /// <summary>
    /// 值结构：<c>{"files":[{"path","sha256","mtime"}]}</c>（按 path ordinal 排序）。
    /// </summary>
    private static JsonElement BuildCanonical(IReadOnlyList<InstructionFile> files)
    {
        var array = new JsonArray();
        foreach (var file in files)
        {
            array.Add(new JsonObject
            {
                ["path"] = file.Path,
                ["sha256"] = Sha256Hex(file.Content),
                ["mtime"] = file.LastModified.ToUnixTimeMilliseconds()
            });
        }

        var root = new JsonObject { ["files"] = array };
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// 从 Canonical 中读出文件指纹（形状不符时返回空集合，并按不可叙述处理）。
    /// </summary>
    private static List<InstructionFileFingerprint> ReadFingerprints(JsonElement? canonical)
    {
        if (canonical is not { } element ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !element.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<InstructionFileFingerprint>(files.GetArrayLength());
        foreach (var item in files.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("path", out var path) ||
                path.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var sha = item.TryGetProperty("sha256", out var shaNode) && shaNode.ValueKind == JsonValueKind.String
                ? shaNode.GetString() ?? string.Empty
                : string.Empty;

            result.Add(new InstructionFileFingerprint(path.GetString()!, sha));
        }

        result.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return result;
    }

    private static Dictionary<string, InstructionFileFingerprint> ToMap(
        IEnumerable<InstructionFileFingerprint> entries) =>
        entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);

    private static DateTimeOffset TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // mtime 仅用于 diff 观测；读取失败退化为 Unix 纪元（不影响正文与哈希）
            return DateTimeOffset.UnixEpoch;
        }
    }

    /// <summary>
    /// 按 <see cref="MaxBaselineChars"/> 截断渲染文本；截断结果即存库文本。
    /// </summary>
    private string Truncate(string text, string cwd)
    {
        if (MaxBaselineChars <= 0 || text.Length <= MaxBaselineChars)
            return text;

        var keep = Math.Max(0, MaxBaselineChars - TruncatedSuffix.Length);
        var truncated = text[..keep] + TruncatedSuffix;

        _logger.LogWarning(
            "项目指令基线超过上限，已截断：{Cwd} 原始={OriginalChars} 上限={MaxChars}",
            cwd,
            text.Length,
            MaxBaselineChars);

        return truncated;
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed record InstructionFileFingerprint(string Path, string Sha256);
}

/// <summary>
/// 指令源本次读取的原始数据（仅渲染使用，不落盘）。
/// </summary>
/// <param name="Cwd">采集时的工作目录（信封属性）。</param>
/// <param name="Files">读取成功的文件（按 path ordinal 排序）。</param>
/// <param name="Unreadable">暂时不可读的路径（供诊断，不参与渲染）。</param>
public sealed record ProjectInstructionsPayload(
    string Cwd,
    IReadOnlyList<InstructionFile> Files,
    IReadOnlyList<InstructionFileReadResult> Unreadable);
