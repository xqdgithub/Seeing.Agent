using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Core.Instructions;

namespace Seeing.Agent.Core.Context.Sources;

/// <summary>
/// 项目指令源（AGENTS.md 等）：<c>Target = Message</c>，初始渲染以用户消息注入，
/// 增量变化以叙述消息追加。
/// <para>读值：逐路径三态（Loaded / Missing / Unreadable）。**只要存在任何不可读路径就整体
/// <see cref="ContextValueState.Unavailable"/>**（宁可保留上次已叙述内容，绝不把「被占用」叙述成「已删除」）。
/// <c>Canonical</c> 只放 <c>{"files":[{"path","sha256"}]}</c>（按 path ordinal 排序）；
/// 正文经 <c>Payload</c> 仅在本次渲染使用、不落盘。</para>
/// <para>扫描根（cwd / workspaceRoot）来自执行作用域 <see cref="IContextExecutionScope"/>：
/// 源不得读进程当前目录（那会让子会话 / 指定 WorkingDirectory 的执行看不到项目指令），
/// 也不得自持可变的「当前根」单例（并发执行会互相覆盖）。</para>
/// </summary>
public sealed class ProjectInstructionsContextSource : IContextSource, IContextSourceMessageMetadata
{
    /// <summary>
    /// 本源稳定键：调用方按此键决定本轮是否跳过本源（强类型，禁止字符串比较）。
    /// </summary>
    public static ContextSourceKey SourceKey { get; } = new(typeof(ProjectInstructionsContextSource).FullName!);

    private const string TruncatedSuffix = "\n…（项目指令内容已按上下文基线上限截断）";

    private readonly IInstructionFileReader _reader;
    private readonly IContextExecutionScope _executionScope;
    private readonly ILogger<ProjectInstructionsContextSource> _logger;

    /// <summary>
    /// 创建项目指令源。
    /// </summary>
    /// <param name="reader">逐路径读取端口（区分缺失与不可读）。</param>
    /// <param name="executionScope">执行作用域（会话 cwd + 工作区根；由宿主在装配前填充）。</param>
    /// <param name="logger">源日志。</param>
    public ProjectInstructionsContextSource(
        IInstructionFileReader reader,
        IContextExecutionScope executionScope,
        ILogger<ProjectInstructionsContextSource> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(executionScope);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _executionScope = executionScope;
        _logger = logger;
    }

    /// <inheritdoc />
    public ContextSourceKey Key => SourceKey;

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
        var cwd = _executionScope.Cwd;
        var workspaceRoot = _executionScope.WorkspaceRoot;

        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(workspaceRoot))
        {
            // 无扫描根：不参与 diff（保留上次叙述），并记录可诊断的事实
            _logger.LogDebug(
                "指令扫描根不完整，跳过本源：Cwd={Cwd} WorkspaceRoot={WorkspaceRoot}",
                cwd,
                workspaceRoot);
            return Unavailable();
        }

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
            .Select(ToInstructionFile)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();

        var unreadable = results
            .Where(result => result.Status == InstructionFileReadStatus.Unreadable)
            .ToArray();

        if (unreadable.Length > 0)
        {
            // Review Focus #1 / spec §8.5：任一文件不可读 → 整体保留旧值。
            // 绝不能只把「已读到的文件」当 canonical：从 canonical 消失的文件会被 Render 判定为删除，
            // 从而把「被占用」叙述成「已不再适用」——比漏叙述更糟。
            _logger.LogWarning(
                "项目指令存在不可读文件，整体按不可用处理（保留上次已叙述内容）：不可读={UnreadableCount} 已读={LoadedCount} 缺失={MissingCount} Cwd={Cwd}",
                unreadable.Length,
                loaded.Length,
                results.Count - loaded.Length - unreadable.Length,
                cwd);

            return new ContextSourceValue(
                ContextValueState.Unavailable,
                null,
                new ProjectInstructionsPayload(cwd, loaded, unreadable));
        }

        if (loaded.Length == 0)
        {
            // 全部确认缺失：返回 Removed 参与 diff（从未建立基线时装配器不会叙述）
            return new ContextSourceValue(
                ContextValueState.Removed,
                null,
                new ProjectInstructionsPayload(cwd, [], []));
        }

        return new ContextSourceValue(
            ContextValueState.Value,
            BuildCanonical(loaded),
            new ProjectInstructionsPayload(cwd, loaded, []));
    }

    /// <inheritdoc />
    public string RenderInitial(ContextSourceValue value)
    {
        var payload = value.Payload as ProjectInstructionsPayload;
        IReadOnlyList<InstructionFile> files = payload?.Files ?? [];
        var cwd = payload?.Cwd ?? _executionScope.Cwd;

        // 截断在渲染内部完成：截断后的文本才是存库基线文本（装配器只观测、不再二次裁剪）
        return RenderEnvelope(cwd, ProjectInstructions.Reasons.Initial, files);
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
                            previousEntry is not null &&
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

    /// <summary>
    /// 为落库消息提供既有 <c>project-instructions</c> 元数据（UI 折叠与标题服务的既有契约）。
    /// <para>元数据从源自己渲染的信封解析得到：重放路径下 <c>Payload</c> 不可用，
    /// 但落库文本一定带有完整信封，因此这是唯一在两条路径上都成立的事实来源。</para>
    /// </summary>
    public IReadOnlyDictionary<string, object>? GetMessageMetadata(string renderedText, ContextSourceValue value)
    {
        if (!ProjectInstructionsRenderer.TryParse(renderedText, out var parts))
            return null;

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ProjectInstructions.MetadataKeys.ProjectInstructions] = true,
            [ProjectInstructions.MetadataKeys.Reason] = parts.Reason,
            [ProjectInstructions.MetadataKeys.Cwd] = parts.Cwd,
            [ProjectInstructions.MetadataKeys.Paths] =
                JsonSerializer.Serialize(parts.Files.Select(file => file.Path))
        };
    }

    private static ContextSourceValue Unavailable() =>
        new(ContextValueState.Unavailable, null, null);

    private static InstructionFile ToInstructionFile(InstructionFileReadResult result) => new()
    {
        Path = result.Path,
        Content = result.Content!,
        Hash = $"sha256:{Sha256Hex(result.Content!)}",
        // 仅作 UI / 诊断使用：不进入 Canonical（否则 touch 一次就会造成永久「每轮重试」）
        LastModified = TryGetLastWriteTimeUtc(result.Path)
    };

    /// <summary>
    /// 值结构：<c>{"files":[{"path","sha256"}]}</c>（按 path ordinal 排序）。
    /// <para><b>不含 mtime</b>：diff 只认内容哈希；mtime 若进入 canonical，文件被 touch
    /// （内容不变）就会产生「检测到变化但无需叙述」的永久未完成状态（每轮重试、永不收敛）。</para>
    /// </summary>
    private static JsonElement BuildCanonical(IReadOnlyList<InstructionFile> files)
    {
        var array = new JsonArray();
        foreach (var file in files)
        {
            array.Add(new JsonObject
            {
                ["path"] = file.Path,
                ["sha256"] = Sha256Hex(file.Content)
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
            // mtime 仅用于诊断；读取失败退化为 Unix 纪元（不影响正文与哈希）
            return DateTimeOffset.UnixEpoch;
        }
    }

    /// <summary>
    /// 渲染完整信封；超出上限时按**文件块**截断，末尾仍保留 <c>&lt;/file&gt;</c> 与
    /// <c>&lt;/project-instructions&gt;</c>，保证模型看到的仍是可解析的完整 XML。
    /// </summary>
    private string RenderEnvelope(string cwd, string reason, IReadOnlyList<InstructionFile> files)
    {
        var full = ProjectInstructionsRenderer.Wrap(cwd, reason, files);
        if (MaxBaselineChars <= 0 || full.Length <= MaxBaselineChars)
            return full;

        var included = new List<InstructionFile>(files.Count);
        var used = ProjectInstructionsRenderer.Wrap(cwd, reason, []).Length;

        foreach (var file in files)
        {
            var blockLength = ProjectInstructionsRenderer.RenderFileBlocks([file]).Length;
            if (used + blockLength <= MaxBaselineChars)
            {
                included.Add(file);
                used += blockLength;
                continue;
            }

            var truncated = TruncateFileToFit(file, MaxBaselineChars - used);
            if (truncated is not null)
                included.Add(truncated);
            break;
        }

        _logger.LogWarning(
            "项目指令基线超过上限，已按文件块截断：{Cwd} 原始字符={OriginalChars} 上限={MaxChars} 保留文件={KeptFiles}/{TotalFiles}",
            cwd,
            full.Length,
            MaxBaselineChars,
            included.Count,
            files.Count);

        return ProjectInstructionsRenderer.Wrap(cwd, reason, included);
    }

    /// <summary>
    /// 在给定块预算内放下一个带截断标记的文件块；放不下（连标记都放不下）时返回 null。
    /// <para>标记写在 <c>&lt;file&gt;</c> 正文末尾，因此截断不会破坏信封结构。</para>
    /// </summary>
    private static InstructionFile? TruncateFileToFit(InstructionFile file, int blockBudget)
    {
        if (blockBudget <= 0)
            return null;

        var content = file.Content ?? string.Empty;
        if (Measure(file, content) <= blockBudget)
            return file;

        var shortest = content[..0] + TruncatedSuffix;
        if (Measure(file, shortest) > blockBudget)
            return null;

        // 内容截断长度对块长度单调：二分找最大可容纳的正文前缀
        var low = 0;
        var high = content.Length - 1;
        var best = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Measure(file, content[..mid] + TruncatedSuffix) <= blockBudget)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return new InstructionFile
        {
            Path = file.Path,
            Content = content[..best] + TruncatedSuffix,
            Hash = file.Hash,
            LastModified = file.LastModified
        };
    }

    private static int Measure(InstructionFile file, string content) =>
        ProjectInstructionsRenderer.RenderFileBlocks([WithContent(file, content)]).Length;

    private static InstructionFile WithContent(InstructionFile file, string content) => new()
    {
        Path = file.Path,
        Content = content,
        Hash = file.Hash,
        LastModified = file.LastModified
    };

    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed record InstructionFileFingerprint(string Path, string Sha256);
}

/// <summary>
/// 指令源本次读取的原始数据（仅渲染使用，不落盘）。
/// </summary>
/// <param name="Cwd">采集时的工作目录（信封属性）。</param>
/// <param name="Files">读取成功的文件（按 path ordinal 排序）。</param>
/// <param name="Unreadable">暂时不可读的路径（诊断用；任一不可读时整体状态为不可用，不参与渲染）。</param>
public sealed record ProjectInstructionsPayload(
    string Cwd,
    IReadOnlyList<InstructionFile> Files,
    IReadOnlyList<InstructionFileReadResult> Unreadable);
