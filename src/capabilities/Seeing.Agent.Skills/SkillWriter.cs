using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;

namespace Seeing.Agent.Skills;

/// <summary>
/// <see cref="ISkillWriter"/> 实现：文件通道的<b>唯一</b>写入口。
/// <para>职责边界：只碰磁盘与 <c>skill-state.json</c>，不解析网络内容、不持有技能字典；
/// 每次成功写入后刷新 <see cref="ISkillCatalog"/> 并返回目录中的真实条目，保证「返回即一致」。</para>
/// <para>安全：名称正则 + 根内判定 + 禁 <c>..</c> / 绝对路径；原子写（同根临时目录 → <c>Directory.Move</c>）；
/// 失败一律清理临时目录，不留半成品（半写目录会被扫描器记为 <c>SkillLoadIssue</c>）。</para>
/// <para>注册通道（插件 / 内嵌资源）技能只读：任何编辑 / 删除都以「该技能由插件提供」拒绝。</para>
/// </summary>
public sealed class SkillWriter : ISkillWriter
{
    /// <summary>注册通道技能的只读提示。</summary>
    private const string PluginReadOnlyError = "该技能由插件提供，不支持编辑/删除";

    /// <summary>技能文件名（与扫描器共用同一常量）。</summary>
    private const string SkillFileName = SkillFileScanner.SkillFileName;

    /// <summary>技能名称最大长度（与扫描器一致）。</summary>
    private const int MaxNameLength = 64;

    /// <summary>技能名称正则：小写字母数字 + 单个连字符分隔。</summary>
    private static readonly Regex s_namePattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>名称中禁止出现的路径 / 平台保留字符。</summary>
    private static readonly char[] s_forbiddenNameChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private readonly ILogger<SkillWriter> _logger;
    private readonly ISkillCatalog _catalog;
    private readonly ISeeingDirectories? _directories;
    private readonly SkillWriteGate _gate;

    /// <summary>
    /// 创建写入服务。
    /// </summary>
    /// <param name="logger">日志器。</param>
    /// <param name="catalog">技能目录（写后刷新，读取现有条目）。</param>
    /// <param name="directories">配置目录契约（决定两级技能根与 <c>skill-state.json</c> 位置）。</param>
    /// <param name="gate">状态文件写闸（并发开关串行化）。</param>
    public SkillWriter(
        ILogger<SkillWriter> logger,
        ISkillCatalog catalog,
        ISeeingDirectories? directories,
        SkillWriteGate gate)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _directories = directories;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    // ===== 创建 =====

    /// <inheritdoc />
    public async Task<SkillWriteResult> CreateAsync(
        SkillDraft draft,
        ConfigLevel level,
        bool overwrite,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!TryValidateName(draft.Name, out var nameError))
            return Failure(nameError);
        if (string.IsNullOrWhiteSpace(draft.Description))
            return Failure("技能描述不能为空");

        if (CheckWritable(draft.Name) is { } rejection)
            return rejection;

        if (!TryResolveRoot(level, out var root, out var rootError))
            return Failure(rootError);

        return await WriteNewSkillAsync(draft.Name, BuildMarkdown(draft), root, overwrite, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SkillWriteResult> WriteMarkdownAsync(
        string name,
        string rawContent,
        ConfigLevel level,
        bool overwrite,
        CancellationToken ct = default)
    {
        if (!TryValidateName(name, out var nameError))
            return Failure(nameError);
        if (string.IsNullOrEmpty(rawContent))
            return Failure("SKILL.md 内容不能为空");

        if (CheckWritable(name) is { } rejection)
            return rejection;

        if (!TryResolveRoot(level, out var root, out var rootError))
            return Failure(rootError);

        // 原样写入：不解析、不重建 frontmatter（未知字段必须保留）
        return await WriteNewSkillAsync(name, rawContent, root, overwrite, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SkillWriteResult> UpdateAsync(
        string name,
        SkillDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!TryValidateName(name, out var nameError))
            return Failure(nameError);
        if (!string.Equals(draft.Name, name, StringComparison.Ordinal))
            return Failure("更新技能不支持改名：请删除后重建，或保持名称一致");
        if (string.IsNullOrWhiteSpace(draft.Description))
            return Failure("技能描述不能为空");

        var entry = _catalog.Find(name);
        if (entry is null)
            return Failure($"技能不存在：{name}");
        if (entry.Source == SkillSource.Registered)
            return Failure($"{PluginReadOnlyError}：{name}");
        if (string.IsNullOrWhiteSpace(entry.DirectoryPath) || !Directory.Exists(entry.DirectoryPath))
            return Failure($"技能目录不存在：{name}");

        var directory = entry.DirectoryPath;
        var target = Path.Combine(directory, SkillFileName);
        // 同目录临时文件 → File.Move：只替换 SKILL.md，目录内其它文件保持不动
        var temp = Path.Combine(directory, $"{SkillFileName}.tmp-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(temp, BuildMarkdown(draft), new UTF8Encoding(false), ct)
                .ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新技能失败：{Name} 目录={Directory}", name, directory);
            return Failure($"更新技能失败：{ex.Message}");
        }
        finally
        {
            TryDeleteFile(temp);
        }

        return await PublishAsync(name, directory, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SkillWriteResult> DeleteAsync(string name, CancellationToken ct = default)
    {
        if (!TryValidateName(name, out var nameError))
            return Failure(nameError);

        var entry = _catalog.Find(name);
        if (entry is null)
            return Failure($"技能不存在：{name}");
        if (entry.Source == SkillSource.Registered)
            return Failure($"{PluginReadOnlyError}：{name}");

        var directory = entry.DirectoryPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Failure($"技能目录不存在：{name}");

        // 只允许删除两级技能根之下的目录：拒绝任何越界删除
        if (!IsUnderAnySkillRoot(directory))
        {
            _logger.LogWarning("拒绝删除技能根之外的目录：{Directory}", directory);
            return Failure($"拒绝删除技能根之外的目录：{directory}");
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "删除技能失败：{Name} 目录={Directory}", name, directory);
            return Failure($"删除技能失败：{ex.Message}");
        }

        await _catalog.RefreshAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("技能已删除：{Name} 目录={Directory}", name, directory);
        return new SkillWriteResult(true, null, null);
    }

    // ===== ZIP 安装 =====

    /// <inheritdoc />
    public async Task<SkillWriteResult> InstallZipAsync(
        string name,
        Stream zip,
        ConfigLevel level,
        bool overwrite,
        string? subdirectoryPrefix,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zip);

        if (!TryValidateName(name, out var nameError))
            return Failure(nameError);
        if (CheckWritable(name) is { } rejection)
            return rejection;
        if (!TryResolveRoot(level, out var root, out var rootError))
            return Failure(rootError);

        if (!TryPrepareTarget(root, name, overwrite, out var target, out var targetError))
            return Failure(targetError);

        var prefix = NormalizePrefix(subdirectoryPrefix);
        var temp = Path.Combine(root, $"{name}.tmp-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temp);
            var extracted = 0;

            using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // 目录项（Name 为空）无需落盘，创建父目录时自然生成
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;

                    var entryPath = entry.FullName.Replace('\\', '/');
                    string relativePath;
                    if (prefix is null)
                    {
                        relativePath = entryPath;
                    }
                    else if (entryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = entryPath[prefix.Length..];
                    }
                    else
                    {
                        // 前缀之外的条目（仓库根 README / 其它技能）不属于本次安装
                        continue;
                    }

                    relativePath = relativePath.TrimStart('/');
                    if (relativePath.Length == 0)
                        continue;

                    // zip-slip：解压目标必须留在临时目录之内
                    var destination = Path.GetFullPath(Path.Combine(temp, relativePath));
                    if (!IsUnder(destination, temp))
                    {
                        throw new InvalidDataException(
                            $"ZIP 包含非法路径（zip-slip）：{entry.FullName}");
                    }

                    var destinationDirectory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                        Directory.CreateDirectory(destinationDirectory);

                    await using var source = entry.Open();
                    await using var destinationStream = File.Create(destination);
                    await source.CopyToAsync(destinationStream, ct).ConfigureAwait(false);
                    extracted++;
                }
            }

            if (extracted == 0)
            {
                return Failure(prefix is null
                    ? "ZIP 中未找到可安装的文件"
                    : $"ZIP 中未找到与指定前缀匹配的文件：{prefix}");
            }

            if (!File.Exists(Path.Combine(temp, SkillFileName)))
            {
                return Failure(prefix is null
                    ? "ZIP 中缺少 SKILL.md"
                    : $"ZIP 的指定前缀下缺少 SKILL.md：{prefix}");
            }

            MoveIntoPlace(temp, target, root, overwrite);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            _logger.LogError(ex, "安装技能 ZIP 失败（内容非法）：{Name}", name);
            return Failure(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "安装技能 ZIP 失败：{Name} 根={Root}", name, root);
            return Failure($"安装技能 ZIP 失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(temp);
        }

        return await PublishAsync(name, target, ct).ConfigureAwait(false);
    }

    // ===== 启用 / 禁用 =====

    /// <inheritdoc />
    public async Task<SkillWriteResult> SetEnabledAsync(
        string name,
        bool enabled,
        ConfigLevel level,
        CancellationToken ct = default)
    {
        if (!TryValidateName(name, out var nameError))
            return Failure(nameError);
        if (_directories is null)
            return Failure("未配置技能目录（ISeeingDirectories），无法修改启用状态");
        if (_catalog.Find(name) is null)
            return Failure($"技能不存在：{name}");

        try
        {
            return await _gate.RunAsync(async token =>
            {
                if (enabled)
                {
                    // 启用必须清除「所有」层级：任一层残留禁用项都会让技能继续不可用
                    await UpdateDisabledStateAsync(_directories.UserSeeingDirectory, name, disable: false, token)
                        .ConfigureAwait(false);
                    await UpdateDisabledStateAsync(_directories.ProjectSeeingDirectory, name, disable: false, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    // 禁用只写调用方指定的层级（另一层保持不动）
                    await UpdateDisabledStateAsync(SeeingDirectoryOf(level), name, disable: true, token)
                        .ConfigureAwait(false);
                }

                var snapshot = await _catalog.RefreshAsync(token).ConfigureAwait(false);
                var entry = snapshot.Entries.FirstOrDefault(e =>
                    string.Equals(e.Info.Name, name, StringComparison.OrdinalIgnoreCase));

                _logger.LogInformation(
                    "技能启用状态已更新：{Name} 启用={Enabled} 层级={Level}",
                    name,
                    enabled,
                    level);

                return entry is null
                    ? Failure($"状态已保存，但目录中找不到技能：{name}")
                    : new SkillWriteResult(true, null, entry);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "保存技能启用状态失败：{Name} 层级={Level}", name, level);
            return Failure($"保存技能启用状态失败：{ex.Message}");
        }
    }

    // ===== 原子落位 =====

    /// <summary>
    /// 写新技能：临时目录内写 SKILL.md → <see cref="Directory.Move(string, string)"/> 落位。
    /// <para>任何失败都在 <c>finally</c> 清理临时目录，避免留下「目录已建、文件未就绪」的半成品。</para>
    /// </summary>
    private async Task<SkillWriteResult> WriteNewSkillAsync(
        string name,
        string markdown,
        string root,
        bool overwrite,
        CancellationToken ct)
    {
        if (!TryPrepareTarget(root, name, overwrite, out var target, out var targetError))
            return Failure(targetError);

        var temp = Path.Combine(root, $"{name}.tmp-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temp);
            await File.WriteAllTextAsync(
                Path.Combine(temp, SkillFileName),
                markdown,
                new UTF8Encoding(false),
                ct).ConfigureAwait(false);

            MoveIntoPlace(temp, target, root, overwrite);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入技能失败：{Name} 根={Root}", name, root);
            return Failure($"写入技能失败：{ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(temp);
        }

        return await PublishAsync(name, target, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把临时目录落位到目标：覆盖时先把旧目录移到备份名，落位成功后再删备份；
    /// 落位失败则回滚旧目录，绝不留下「新目录一半、旧目录丢失」的中间态。
    /// </summary>
    private void MoveIntoPlace(string temp, string target, string root, bool overwrite)
    {
        // 目标被同名文件占用时 Directory.Move 会失败；这里显式抛出更清晰的原因
        if (File.Exists(target))
            throw new IOException($"目标路径已被文件占用：{target}");

        string? backup = null;

        if (Directory.Exists(target))
        {
            if (!overwrite)
                throw new IOException($"技能目录已存在：{target}");

            backup = Path.Combine(root, $"{Path.GetFileName(target)}.bak-{Guid.NewGuid():N}");
            Directory.Move(target, backup);
        }

        try
        {
            Directory.Move(temp, target);
        }
        catch
        {
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(target))
            {
                try
                {
                    Directory.Move(backup, target);
                    backup = null;
                }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    // 回滚失败必须可观测：旧目录仍在备份名下，人工可恢复
                    _logger.LogError(restoreEx, "回滚技能目录失败，旧目录保留在备份路径：{Backup}", backup);
                }
            }

            throw;
        }

        if (backup is not null)
            TryDeleteDirectory(backup);
    }

    /// <summary>刷新目录并取出写入后的条目；取不到时带出扫描问题作为失败原因。</summary>
    private async Task<SkillWriteResult> PublishAsync(
        string name,
        string targetDirectory,
        CancellationToken ct)
    {
        var snapshot = await _catalog.RefreshAsync(ct).ConfigureAwait(false);
        var entry = snapshot.Entries.FirstOrDefault(e =>
            string.Equals(e.Info.Name, name, StringComparison.OrdinalIgnoreCase));

        if (entry is not null)
        {
            _logger.LogInformation("技能已写入：{Name} 来源={Source} 目录={Directory}", name, entry.Source, entry.DirectoryPath);
            return new SkillWriteResult(true, null, entry);
        }

        var issue = snapshot.Issues.FirstOrDefault(i =>
            IsUnder(i.Path, targetDirectory) ||
            string.Equals(i.Path, targetDirectory, StringComparison.OrdinalIgnoreCase));

        var reason = issue?.Reason ?? "技能已写入但目录未能识别（请检查 SKILL.md 的 name / description）";
        _logger.LogWarning(
            "技能写入后未出现在目录中：{Name} 目录={Directory} 原因={Reason}",
            name,
            targetDirectory,
            reason);

        return Failure(reason);
    }

    // ===== 目标解析与校验 =====

    /// <summary>注册通道技能只读：命中即返回拒绝结果。</summary>
    private SkillWriteResult? CheckWritable(string name)
    {
        var entry = _catalog.Find(name);
        if (entry?.Source != SkillSource.Registered)
            return null;

        _logger.LogWarning("拒绝对注册通道技能写入：{Name} 提供方={ProviderId}", name, entry.ProviderId);
        return Failure($"{PluginReadOnlyError}：{name}");
    }

    /// <summary>解析层级对应的技能根（{levelSeeing}/skills）。</summary>
    private bool TryResolveRoot(ConfigLevel level, out string root, out string error)
    {
        root = string.Empty;
        error = string.Empty;

        if (_directories is null)
        {
            error = "未配置技能目录（ISeeingDirectories），无法写入";
            return false;
        }

        var seeingDirectory = SeeingDirectoryOf(level);
        if (string.IsNullOrWhiteSpace(seeingDirectory))
        {
            error = $"未配置 {level} 级配置目录，无法写入";
            return false;
        }

        root = Path.Combine(seeingDirectory, "skills");
        return true;
    }

    private string SeeingDirectoryOf(ConfigLevel level) =>
        level == ConfigLevel.Project
            ? _directories!.ProjectSeeingDirectory
            : _directories!.UserSeeingDirectory;

    /// <summary>计算并校验目标目录（必须在根之内；已存在且不允许覆盖时提前失败）。</summary>
    private static bool TryPrepareTarget(
        string root,
        string name,
        bool overwrite,
        out string target,
        out string error)
    {
        error = string.Empty;
        target = Path.GetFullPath(Path.Combine(root, name));

        if (!IsUnder(target, root))
        {
            error = $"技能目标目录越出技能根：{target}";
            return false;
        }

        if (Directory.Exists(target) && !overwrite)
        {
            error = $"技能目录已存在：{name}（如需覆盖请显式指定覆盖）";
            return false;
        }

        return true;
    }

    /// <summary>技能名称校验：非空、长度、无路径分隔符 / 上级引用、符合正则。</summary>
    private static bool TryValidateName(string? name, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "技能名称不能为空";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"技能名称过长（{name.Length} > {MaxNameLength}）";
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(name)
            || name.IndexOfAny(s_forbiddenNameChars) >= 0)
        {
            error = $"技能名称不得包含路径分隔符或上级目录引用：{name}";
            return false;
        }

        if (!s_namePattern.IsMatch(name))
        {
            error = $"技能名称只能包含小写字母、数字和连字符（如 my-skill）：{name}";
            return false;
        }

        return true;
    }

    // ===== 状态文件（唯一写者） =====

    /// <summary>
    /// 更新某一级的禁用集合：启用 → 移除；禁用 → 加入。内容未变则不写盘。
    /// <para>调用方必须已持有 <see cref="SkillWriteGate"/>：整文件覆盖写不能与并发开关交错。</para>
    /// </summary>
    private async Task UpdateDisabledStateAsync(
        string seeingDirectory,
        string name,
        bool disable,
        CancellationToken ct)
    {
        var filePath = Path.Combine(seeingDirectory, SkillCatalogCore.SkillStateFileName);
        var disabled = await LoadDisabledNamesAsync(filePath, ct).ConfigureAwait(false);

        var changed = disable ? disabled.Add(name) : disabled.Remove(name);
        if (!changed)
            return;

        await SaveDisabledNamesAsync(filePath, disabled, ct).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> LoadDisabledNamesAsync(string filePath, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath))
            return result;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var data = System.Text.Json.JsonSerializer.Deserialize<DisabledSkillsData>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

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
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            // 状态文件损坏 / 不可读：按空集合处理（可观测），避免整次开关直接失败
            _logger.LogWarning(ex, "读取技能禁用状态失败，按空集合处理：{Path}", filePath);
        }

        return result;
    }

    private async Task SaveDisabledNamesAsync(string filePath, HashSet<string> disabled, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // 排序保证字节稳定（同内容不产生无意义 diff）
        var data = new DisabledSkillsData
        {
            DisabledSkills = disabled.OrderBy(name => name, StringComparer.Ordinal).ToList()
        };
        var json = System.Text.Json.JsonSerializer.Serialize(
            data,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
        _logger.LogInformation("技能禁用状态已保存：{Path} 数量={Count}", filePath, data.DisabledSkills.Count);
    }

    /// <summary>禁用状态落盘模型（沿用既有格式）。</summary>
    private sealed class DisabledSkillsData
    {
        public List<string>? DisabledSkills { get; set; }
    }

    // ===== 路径工具 =====

    /// <summary>ZIP 前缀归一化：统一分隔符、去前导斜杠、补尾部斜杠；空值表示整包。</summary>
    private static string? NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        var normalized = prefix.Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Length == 0)
            return null;

        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    /// <summary>目录是否位于任一级技能根之下（删除操作的越界保护）。</summary>
    private bool IsUnderAnySkillRoot(string directory)
    {
        if (_directories is null)
            return false;

        return IsUnder(directory, Path.Combine(_directories.UserSeeingDirectory, "skills"))
            || IsUnder(directory, Path.Combine(_directories.ProjectSeeingDirectory, "skills"));
    }

    /// <summary>规范化后判断 <paramref name="path"/> 是否严格位于 <paramref name="root"/> 之内。</summary>
    private static bool IsUnder(string path, string root)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 非法路径按「不在根内」处理（拒绝写入 / 删除）
            return false;
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败：记录后继续（临时目录残留不影响已落位结果，但必须可观测）
            _logger.LogWarning(ex, "清理技能临时目录失败：{Directory}", directory);
        }
    }

    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "清理技能临时文件失败：{Path}", filePath);
        }
    }

    // ===== 内容生成 =====

    /// <summary>由草稿生成 SKILL.md（frontmatter + 空行 + 正文）。</summary>
    private static string BuildMarkdown(SkillDraft draft)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: ").Append(FormatYamlScalar(draft.Name)).Append('\n');
        builder.Append("description: ").Append(FormatYamlScalar(draft.Description)).Append('\n');

        if (draft.Metadata is not null)
        {
            foreach (var (key, value) in draft.Metadata)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                builder.Append(key).Append(": ").Append(FormatYamlScalar(value ?? string.Empty)).Append('\n');
            }
        }

        builder.Append("---\n\n");
        builder.Append(draft.Content ?? string.Empty);
        return builder.ToString();
    }

    /// <summary>YAML 标量转义：仅在必要时加双引号，避免无意义地改写用户描述。</summary>
    private static string FormatYamlScalar(string value)
    {
        if (value.Length == 0)
            return "\"\"";

        var needsQuote = value.IndexOfAny([':', '#', '"', '\'', '\n', '\r', '\t']) >= 0
            || value[0] == ' '
            || value[^1] == ' '
            || "-?&*!|>%@`[]{}".IndexOf(value[0]) >= 0;

        if (!needsQuote)
            return value;

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static SkillWriteResult Failure(string error) => new(false, error, null);
}
