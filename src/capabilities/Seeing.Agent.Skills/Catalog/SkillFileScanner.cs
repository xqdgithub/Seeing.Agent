using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Skills;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// 技能文件根（目录 + 来源分类）。列表顺序即优先级：索引越小优先级越高。
/// </summary>
/// <param name="Directory">技能根目录（绝对路径）。</param>
/// <param name="Source">该根下技能的来源分类。</param>
public sealed record SkillRoot(string Directory, SkillSource Source);

/// <summary>
/// 一次扫盘的原始结果（未合并注册通道）。
/// </summary>
/// <param name="Entries">按根优先级排序的条目（同名已去重）。</param>
/// <param name="Conflicts">被忽略的同名技能。</param>
/// <param name="Issues">解析 / 读取失败。</param>
public sealed record SkillScanResult(
    IReadOnlyList<SkillCatalogEntry> Entries,
    IReadOnlyList<SkillConflict> Conflicts,
    IReadOnlyList<SkillLoadIssue> Issues);

/// <summary>
/// SKILL.md 扫描器：按根优先级扫盘 → 解析 → 同名冲突裁决 → 问题收集。
/// <para>解析规则与既有 <c>SkillManager.ParseSkillFile</c> 完全一致（frontmatter 正则、YamlDotNet、
/// 名称格式、描述必填、64/1024 长度上限、名称与目录名不一致仅告警）。</para>
/// <para>解析失败一律转为 <see cref="SkillLoadIssue"/>，绝不抛出。</para>
/// </summary>
public static class SkillFileScanner
{
    /// <summary>
    /// 技能名称校验：只做安全边界（非空 / 长度 / 字符集），不限制大小写与分隔风格。
    /// </summary>
    /// <param name="name">待校验名称（原样，方法内部会 Trim）。</param>
    /// <param name="error">失败原因（可直接展示）。</param>
    private static bool TryValidateName(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "缺少 name 字段";
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            error = $"技能名称过长（{trimmed.Length} > {MaxNameLength}）";
            return false;
        }

        if (s_nameInvalidChars.IsMatch(trimmed))
        {
            error = $"技能名称含非法字符（不能包含 \\ / : * ? \" < > | 或控制字符）：{trimmed}";
            return false;
        }

        return true;
    }

    /// <summary>技能文件名常量。</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>技能名称里不允许出现的字符：路径分隔符、Windows 非法文件名字符、控制字符。</summary>
    /// <remarks>
    /// 名称只是目录内的键，不要求小写或连字符——生态里常见
    /// <c>Self-Improving + Proactive Agent</c> 这类名称，按格式拒绝会让整条技能加载失败。
    /// </remarks>
    private static readonly Regex s_nameInvalidChars = new(@"[\\/:*?""<>|\x00-\x1F]", RegexOptions.Compiled);

    private static readonly Regex s_frontmatterPattern = new(
        @"^---[\r]?[\n](.*?)[\r]?[\n]---[\r]?[\n]?",
        RegexOptions.Multiline | RegexOptions.Singleline);

    /// <summary>技能名称最大长度。</summary>
    private const int MaxNameLength = 64;

    /// <summary>技能描述最大长度（超出截断）。</summary>
    private const int MaxDescriptionLength = 1024;

    private static readonly SkillCatalogEntry[] s_noEntries = [];

    /// <summary>
    /// 扫描全部根目录下的 SKILL.md。
    /// </summary>
    /// <param name="roots">根列表（优先级从高到低）。</param>
    /// <param name="logger">日志器（解析失败同时告警）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<SkillScanResult> ScanAsync(
        IReadOnlyList<SkillRoot> roots,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(logger);

        var candidates = new List<(SkillCatalogEntry Entry, int RootIndex)>();
        var conflicts = new List<SkillConflict>();
        var issues = new List<SkillLoadIssue>();

        for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var root = roots[rootIndex];
            if (string.IsNullOrWhiteSpace(root.Directory) || !Directory.Exists(root.Directory))
                continue;

            string[] files;
            try
            {
                // 同步目录枚举放入线程池，避免阻塞调用线程（与既有发现逻辑一致）
                files = await Task.Run(
                    () => Directory.GetFiles(root.Directory, SkillFileName, SearchOption.AllDirectories),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 目录不可读：记录问题，继续扫其它根（安装器写到一半 / 权限不足）
                issues.Add(new SkillLoadIssue(root.Directory, $"扫描技能根失败：{ex.Message}"));
                logger.LogWarning(ex, "扫描技能根失败，已跳过：{Directory}", root.Directory);
                continue;
            }

            // 稳定顺序：同名冲突的裁决不受文件枚举顺序影响
            Array.Sort(files, StringComparer.Ordinal);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 安装器写到一半（文件被占用 / 未就绪）不得让装配崩溃
                    issues.Add(new SkillLoadIssue(file, $"读取技能文件失败：{ex.Message}"));
                    logger.LogWarning(ex, "读取技能文件失败，已跳过：{File}", file);
                    continue;
                }

                var info = ParseSkillFile(file, content, logger, out var reason);
                if (info is null || string.IsNullOrEmpty(info.Name))
                {
                    issues.Add(new SkillLoadIssue(file, reason ?? "技能解析失败"));
                    continue;
                }

                candidates.Add((
                    new SkillCatalogEntry(
                        info,
                        root.Source,
                        ProviderId: null,
                        DirectoryPath: Path.GetDirectoryName(file) ?? string.Empty),
                    rootIndex));
            }
        }

        if (candidates.Count == 0)
            return new SkillScanResult(s_noEntries, conflicts, issues);

        // 同名裁决：根优先级高者胜出（索引小者优先；同索引按文件路径排序已确定）
        var winners = new Dictionary<string, (SkillCatalogEntry Entry, int RootIndex)>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<(SkillCatalogEntry Entry, int RootIndex)>();

        foreach (var candidate in candidates)
        {
            if (winners.TryGetValue(candidate.Entry.Info.Name, out var existing))
            {
                conflicts.Add(new SkillConflict(
                    candidate.Entry.Info.Name,
                    existing.Entry.Source,
                    existing.Entry.DirectoryPath,
                    candidate.Entry.Source,
                    candidate.Entry.DirectoryPath));
                logger.LogWarning(
                    "技能名冲突，保留高优先级条目：{Name} 保留={Winner} 忽略={Loser}",
                    candidate.Entry.Info.Name,
                    existing.Entry.DirectoryPath,
                    candidate.Entry.DirectoryPath);
                continue;
            }

            winners[candidate.Entry.Info.Name] = candidate;
            ordered.Add(candidate);
        }

        return new SkillScanResult(
            ordered.Select(c => c.Entry).ToArray(),
            conflicts,
            issues);
    }

    /// <summary>
    /// 解析 SKILL.md（行为与 <c>SkillManager.ParseSkillFile</c> 逐条一致）。
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="content">文件全文。</param>
    /// <param name="logger">日志器。</param>
    /// <param name="reason">失败原因（成功时为 null）。</param>
    /// <returns>解析成功返回技能信息；失败返回 null。</returns>
    public static SkillInfo? ParseSkillFile(
        string filePath,
        string content,
        ILogger logger,
        out string? reason)
    {
        reason = null;

        var frontmatterMatch = s_frontmatterPattern.Match(content);
        var info = new SkillInfo { Location = filePath };

        if (!frontmatterMatch.Success)
        {
            // 缺少 YAML frontmatter：尝试从目录名推断（既有行为）
            logger.LogWarning("技能文件缺少 YAML frontmatter，尝试从文件名推断: {File}", filePath);

            var skillDirName = Path.GetFileName(Path.GetDirectoryName(filePath));
            if (string.IsNullOrEmpty(skillDirName))
            {
                logger.LogWarning("无法从路径推断技能名称: {File}", filePath);
                reason = "缺少 YAML frontmatter，且无法从目录名推断技能名称";
                return null;
            }

            info.Name = skillDirName;
            info.Description = $"技能: {skillDirName}（从目录名自动生成）";
            info.Content = content.Trim();

            if (!TryValidateName(info.Name, out var inferredError))
            {
                logger.LogWarning("推断的技能名称不可用: {Name}（{Reason}）", info.Name, inferredError);
                reason = $"缺少 YAML frontmatter，且目录名不可作为技能名称：{skillDirName}（{inferredError}）";
                return null;
            }

            info.Name = info.Name.Trim();
            return info;
        }

        info.Content = content.Substring(frontmatterMatch.Length).Trim();
        var frontmatter = frontmatterMatch.Groups[1].Value;

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var yamlData = deserializer.Deserialize<Dictionary<string, object?>>(frontmatter);
            if (yamlData == null)
            {
                logger.LogWarning("YAML frontmatter 解析结果为空: {File}", filePath);
                reason = "YAML frontmatter 解析结果为空";
                return null;
            }

            if (yamlData.TryGetValue("name", out var nameObj) && nameObj is string name)
                info.Name = name;

            if (yamlData.TryGetValue("description", out var descObj) && descObj is string description)
                info.Description = description;

            if (yamlData.TryGetValue("version", out var versionObj) && versionObj is string version)
                info.Version = version;

            if (yamlData.TryGetValue("author", out var authorObj) && authorObj is string author)
                info.Author = author;

            if (yamlData.TryGetValue("license", out var licenseObj) && licenseObj is string license)
                info.License = license;

            if (yamlData.TryGetValue("compatibility", out var compatObj) && compatObj is string compatibility)
                info.Compatibility = compatibility;

            if (yamlData.TryGetValue("tags", out var tagsObj) && tagsObj is string tagsStr)
            {
                info.Tags = tagsStr.Split(',')
                    .Select(t => t.Trim())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();
            }

            if (yamlData.TryGetValue("requires", out var requiresObj) && requiresObj is string requiresStr)
            {
                info.Requires = requiresStr.Split(',')
                    .Select(r => r.Trim())
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToList();
            }

            if (yamlData.TryGetValue("metadata", out var metadataObj) && metadataObj is Dictionary<object, object> metadata)
            {
                info.Metadata = metadata.ToDictionary(
                    kvp => kvp.Key?.ToString() ?? "",
                    kvp => kvp.Value?.ToString() ?? "");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "YAML frontmatter 解析失败: {File}", filePath);
            reason = $"YAML frontmatter 解析失败：{ex.Message}";
            return null;
        }

        // === 验证 ===

        if (!TryValidateName(info.Name, out var nameError))
        {
            logger.LogWarning("技能名称不可用: {Reason}", nameError);
            reason = nameError;
            return null;
        }

        info.Name = info.Name.Trim();

        // 名称与目录名不一致仅告警（既有行为：不拒绝加载）
        var dirName = Path.GetFileName(Path.GetDirectoryName(filePath));
        if (!string.IsNullOrEmpty(dirName) && info.Name != dirName)
        {
            logger.LogWarning(
                "技能名称 '{Name}' 与目录名 '{DirName}' 不匹配，建议保持一致: {File}",
                info.Name,
                dirName,
                filePath);
        }

        if (string.IsNullOrEmpty(info.Description))
        {
            logger.LogWarning("技能缺少 description 字段: {File}", filePath);
            reason = "缺少 description 字段";
            return null;
        }

        if (info.Description.Length > MaxDescriptionLength)
        {
            logger.LogWarning(
                "技能描述过长 ({Length} > {Max})，已截断: {Name}",
                info.Description.Length,
                MaxDescriptionLength,
                info.Name);
            info.Description = info.Description.Substring(0, MaxDescriptionLength);
        }

        return info;
    }
}
