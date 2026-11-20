using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Background;
using Seeing.Agent.Configuration;
using Seeing.Agent.Paths;
using Seeing.Files;
using Seeing.Serialization;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// <see cref="IBackgroundActivityStore"/> 的唯一实现：把活动快照原子写到
/// <c>{活动根}/{activityId}.json</c>（spec §11.1）。
/// <para>
/// <b>为什么每次整文件覆盖</b>：快照是「一份记录的当前版本」，不是追加流；原子替换保证
/// 读者要么看到旧版本、要么看到新版本，不会读到半截 JSON。
/// </para>
/// <para>
/// <b>容错</b>：损坏/半截文件只记 Warning 并跳过，绝不因单条坏记录让整个扫描失败——
/// 否则一条坏文件会让所有孤儿活动永远收敛不了。
/// </para>
/// <para>
/// <b>锁</b>：写与条件覆盖都取 <see cref="AtomicFileWrite.GetLock"/>（按绝对路径）。
/// 条件覆盖在锁内重读，语义是「读 → 判 → 覆盖」整段互斥（spec §11.2）。
/// </para>
/// </summary>
internal sealed class BackgroundActivityStore : IBackgroundActivityStore
{
    /// <summary>快照文件后缀。</summary>
    internal const string FileSuffix = BackgroundActivityPaths.SnapshotFileSuffix;

    /// <summary>活动根目录名（位于项目 <c>.seeing</c> 或用户 <c>~/.seeing</c> 下）。</summary>
    internal const string DirectoryName = BackgroundActivityPaths.DirectoryName;

    private static readonly JsonSerializerOptions s_json = SeeingJson.Create(
        indented: true,
        static options => options.Converters.Add(new JsonStringEnumConverter()));

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<BackgroundActivityStore>? _logger;

    public BackgroundActivityStore(
        IWorkspaceProvider workspace,
        ILogger<BackgroundActivityStore>? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SaveAsync(BackgroundActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = ResolveSnapshotPath(snapshot.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var gate = AtomicFileWrite.GetLock(path);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(path, snapshot).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<BackgroundActivitySnapshot?> LoadAsync(string activityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        return ReadSnapshotAsync(ResolveSnapshotPath(activityId));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BackgroundActivitySnapshot>> LoadAllAsync()
    {
        var root = BackgroundActivityPaths.ResolveActivityRoot(_workspace);
        if (!Directory.Exists(root))
            return Array.Empty<BackgroundActivitySnapshot>();

        string[] files;
        try
        {
            files = Directory.GetFiles(root, "*" + FileSuffix, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "后台活动根枚举失败，按无记录处理 Root={Root}", root);
            return Array.Empty<BackgroundActivitySnapshot>();
        }

        var result = new List<BackgroundActivitySnapshot>(files.Length);
        foreach (var path in files)
        {
            var activityId = Path.GetFileNameWithoutExtension(path);
            if (!BackgroundActivityPaths.IsValidActivityId(activityId))
            {
                // 非活动文件（杂项）不参与扫描。
                continue;
            }

            var snapshot = await ReadSnapshotAsync(path).ConfigureAwait(false);
            if (snapshot is null)
                continue;

            // 文件名与内部 id 不一致说明文件被改动过：按来源不可信跳过，避免覆盖到别的记录。
            if (!string.Equals(snapshot.Id, activityId, StringComparison.Ordinal))
            {
                _logger?.LogWarning(
                    "后台活动快照 id 与文件名不一致，跳过 Path={Path} Id={ActivityId}",
                    path,
                    snapshot.Id);
                continue;
            }

            result.Add(snapshot);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceIfUnchangedAsync(
        string activityId,
        BackgroundActivityStatus expectedStatus,
        string expectedHostInstanceId,
        Func<BackgroundActivitySnapshot, BackgroundActivitySnapshot> replacement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityId);
        ArgumentNullException.ThrowIfNull(replacement);

        var path = ResolveSnapshotPath(activityId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var gate = AtomicFileWrite.GetLock(path);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = await ReadSnapshotAsync(path).ConfigureAwait(false);
            if (current is null
                || current.Status != expectedStatus
                || !string.Equals(current.HostInstanceId, expectedHostInstanceId, StringComparison.Ordinal))
            {
                return false;
            }

            var next = replacement(current);
            ArgumentNullException.ThrowIfNull(next, nameof(replacement));

            if (!string.Equals(next.Id, activityId, StringComparison.Ordinal))
                throw new ArgumentException("替换快照的 id 不得改变。", nameof(replacement));

            await WriteAtomicAsync(path, next).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    // ─── 读 ───────────────────────────────────────────────────────────────────

    /// <summary>读取一个快照文件；不存在返回 <see langword="null"/>，损坏则记 Warning 并返回 <see langword="null"/>。</summary>
    private async Task<BackgroundActivitySnapshot?> ReadSnapshotAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            // 与原子替换并发：允许删除共享，避免读者短暂挡住 Move。
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            return await JsonSerializer
                .DeserializeAsync<BackgroundActivitySnapshot>(stream, s_json)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger?.LogWarning(ex, "后台活动快照读取失败，跳过 Path={Path}", path);
            return null;
        }
    }

    // ─── 写 ───────────────────────────────────────────────────────────────────

    /// <summary>写快照；调用方必须已持有该路径的 <see cref="AtomicFileWrite.GetLock"/>。</summary>
    private async Task WriteAtomicAsync(string path, BackgroundActivitySnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, s_json);
        var tempPath = AtomicFileWrite.CreateTempPath(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, json, s_utf8NoBom, CancellationToken.None)
                .ConfigureAwait(false);
            await AtomicFileWrite
                .MoveWithRetryAsync(tempPath, path, snapshot.Id, _logger, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // 不删已成功的目标文件；失败时临时文件是唯一可恢复副本，成功移动后已不存在。
            AtomicFileWrite.TryDelete(tempPath);
            throw;
        }
    }

    // ─── 路径 ─────────────────────────────────────────────────────────────────

    /// <summary>解析并校验快照路径；非法活动 id 与越界路径一律抛异常。</summary>
    private string ResolveSnapshotPath(string activityId)
    {
        if (!BackgroundActivityPaths.IsValidActivityId(activityId))
        {
            throw new ArgumentException(
                $"非法后台活动 id（要求 ^act_[0-9a-f]{{32}}$）：'{activityId}'。",
                nameof(activityId));
        }

        var root = BackgroundActivityPaths.ResolveActivityRoot(_workspace);
        var path = Path.Combine(root, activityId + FileSuffix);

        // 纵深防御：id 已由正则约束，这里再确认拼出的路径确实落在活动根内（符号链接等）。
        if (!PathBoundary.IsStrictlyWithin(path, root))
            throw new UnauthorizedAccessException($"后台活动快照路径越界：'{path}'（根：'{root}'）。");

        return path;
    }
}
