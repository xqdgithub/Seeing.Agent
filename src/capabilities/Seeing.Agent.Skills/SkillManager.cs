using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Prompts;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using System.Text;
using System.Text.Json;

namespace Seeing.Agent.Skills
{
    /// <summary>
    /// 技能只读门面 — 全部答案来自 <see cref="ISkillCatalog"/> 的当前快照，自身不持有技能字典、
    /// 不扫盘、不解析 frontmatter。
    /// <para>
    /// 技能是上下文提供者，不是可执行单元。技能内容通过 SkillTool 注入到 LLM 上下文中。
    /// </para>
    /// <para>
    /// 写面成员（<see cref="AddSearchDirectory"/> / <see cref="DiscoverSkillsAsync"/> /
    /// <see cref="RegisterEmbeddedSkill"/> / <see cref="Unregister"/>）本期保留签名以不打断现有消费者：
    /// 能安全转发到目录的一律转发，无法安全转发的明确抛 <see cref="NotSupportedException"/>。
    /// 移除 / 标注 <c>[Obsolete]</c> 与内嵌技能的异步迁移留给 Task 10（见计划）。
    /// </para>
    /// </summary>
    public class SkillManager : ISkillManager, IPromptSectionContributor
    {
        /// <summary>技能禁用状态文件名（沿用既有落盘格式）。</summary>
        private const string SkillStateFileName = "skill-state.json";

        private readonly ILogger<SkillManager> _logger;
        private readonly SkillCatalogCore _catalog;
        private readonly ISeeingDirectories? _directories;

        /// <summary>禁用状态落盘闸：同一时刻只允许一次读-改-写（避免并发写互相覆盖）。</summary>
        private readonly SemaphoreSlim _stateWriteGate = new(1, 1);

        /// <inheritdoc />
        public string SectionName => PromptSectionNames.Skills;

        /// <inheritdoc />
        public string Heading => "## Skills";

        /// <inheritdoc />
        public PromptSectionPlacement Placement => PromptSectionPlacement.AtEnd;

        /// <inheritdoc />
        public int Order => 200;

        /// <summary>
        /// 创建门面。
        /// </summary>
        /// <param name="logger">门面日志。</param>
        /// <param name="catalog">技能目录（唯一真相源）。</param>
        /// <param name="directories">配置目录契约（用于禁用状态落盘；为空时禁用操作只影响目录内存态）。</param>
        public SkillManager(
            ILogger<SkillManager> logger,
            SkillCatalogCore catalog,
            ISeeingDirectories? directories = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _directories = directories;
        }

        // ===== 提示词分节（Task 9 起由技能上下文源接管整块） =====

        /// <inheritdoc />
        public Task<string?> BuildAsync(PromptContext context, CancellationToken cancellationToken = default)
        {
            var skills = GetAllSkillInfos().Values.ToList();
            if (skills.Count == 0)
                return Task.FromResult<string?>("暂无可用技能。");

            var sb = new StringBuilder();
            sb.AppendLine("以下技能可供使用：");
            sb.AppendLine();

            foreach (var skill in skills)
            {
                sb.AppendLine($"### {skill.Name}");
                sb.AppendLine(skill.Description);

                if (skill.Tags.Count > 0)
                    sb.AppendLine($"**标签**: {string.Join(", ", skill.Tags)}");

                sb.AppendLine();
            }

            return Task.FromResult<string?>(sb.ToString().TrimEnd());
        }

        // ===== 只读投影 =====

        /// <summary>获取所有技能搜索目录（当前生效的根）。</summary>
        public IReadOnlyList<string> GetSkillDirectories() => _catalog.Roots;

        /// <summary>按名称获取技能信息（被禁用的返回 null）。</summary>
        public SkillInfo? GetSkillInfo(string name) =>
            string.IsNullOrWhiteSpace(name) || !IsSkillEnabled(name)
                ? null
                : _catalog.Find(name)?.Info;

        /// <summary>获取技能信息（包括禁用的，供详情页使用）。</summary>
        public SkillInfo? GetSkillInfoIncludingDisabled(string name) =>
            string.IsNullOrWhiteSpace(name) ? null : _catalog.Find(name)?.Info;

        /// <summary>获取所有技能信息（只返回启用的技能）。</summary>
        public IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfos() =>
            Project(snapshot => snapshot.Entries.Where(entry => !snapshot.DisabledNames.Contains(entry.Info.Name)));

        /// <summary>获取所有技能信息（包括禁用的，供 UI 使用）。</summary>
        public IReadOnlyDictionary<string, SkillInfo> GetAllSkillInfosIncludingDisabled() =>
            Project(snapshot => snapshot.Entries);

        /// <summary>合并后的禁用技能集合（用户级 ∪ 项目级），直接读目录快照。</summary>
        public IReadOnlySet<string> DisabledSkills => _catalog.Current.DisabledNames;

        /// <summary>状态变更事件（写盘并重扫目录后触发）。</summary>
        public event Action? OnSkillStateChanged;

        /// <summary>检查技能是否启用（未登记的技能按「未禁用」处理，保持既有语义）。</summary>
        public bool IsSkillEnabled(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName))
                return true;

            return !_catalog.Current.DisabledNames.Contains(skillName);
        }

        /// <summary>
        /// 获取技能目录下的相关文件（排除 SKILL.md；注册通道技能没有磁盘目录）。
        /// </summary>
        public List<string> GetSkillFiles(string skillName, int limit = 10)
        {
            var entry = _catalog.Find(skillName);
            if (entry is null
                || entry.Source == SkillSource.Registered
                || string.IsNullOrWhiteSpace(entry.DirectoryPath)
                || !Directory.Exists(entry.DirectoryPath))
            {
                return [];
            }

            try
            {
                return Directory.GetFiles(entry.DirectoryPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => !Path.GetFileName(file).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    .Take(limit)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "获取技能文件列表失败：{Skill} 目录={Directory}", skillName, entry.DirectoryPath);
                return [];
            }
        }

        // ===== 写面（本期为转发 / 明确拒绝的过渡实现，Task 10 收敛） =====

        /// <summary>
        /// 添加技能搜索目录。
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// 根列表是目录的唯一事实（由加载器一次算出），任何「追加目录」都会造成第二份根定义，故本期拒绝。
        /// </exception>
        public void AddSearchDirectory(string directory)
        {
            // TODO(Task 10)：写面迁移到 ISkillWriter / ISkillCatalog 的显式根管理后再删除本方法。
            throw new NotSupportedException(
                "技能搜索目录不再支持逐个追加：根列表由 SkillLoader 统一计算后经 ISkillCatalog.SetRoots 写入。" +
                "请改用 ISkillCatalog，或在 SkillsOptions.Paths 中配置技能目录。");
        }

        /// <summary>重新扫描并发布目录快照（转发到 <see cref="ISkillCatalog.RefreshAsync"/>）。</summary>
        public async Task DiscoverSkillsAsync(CancellationToken cancellationToken = default)
            => await _catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>刷新技能列表（转发到 <see cref="ISkillCatalog.RefreshAsync"/>）。</summary>
        public async Task RefreshSkillsAsync(CancellationToken cancellationToken = default)
            => await _catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// 注册内嵌技能（模块 EmbeddedResource）。
        /// <para>同步转发到目录的注册通道：目录内部用同步临界区，不做 async-over-sync。</para>
        /// </summary>
        public void RegisterEmbeddedSkill(SkillInfo skillInfo)
        {
            ArgumentNullException.ThrowIfNull(skillInfo);

            if (string.IsNullOrWhiteSpace(skillInfo.Name))
            {
                _logger.LogWarning("技能名无效，拒绝注册内嵌技能：{Location}", skillInfo.Location);
                return;
            }

            skillInfo.Location = "embedded://" + skillInfo.Name.TrimStart('/');
            if (!_catalog.TryRegister(skillInfo, "embedded"))
            {
                _logger.LogWarning("内嵌技能注册被目录拒绝（同名已存在）：{Name}", skillInfo.Name);
            }
        }

        /// <summary>注册技能（手工添加；同步转发到目录注册通道）。</summary>
        public void RegisterSkill(SkillInfo skillInfo)
        {
            ArgumentNullException.ThrowIfNull(skillInfo);

            if (string.IsNullOrWhiteSpace(skillInfo.Name))
            {
                _logger.LogWarning("技能名无效，拒绝注册技能：{Location}", skillInfo.Location);
                return;
            }

            if (!_catalog.TryRegister(skillInfo, "manual"))
            {
                _logger.LogWarning("技能注册被目录拒绝（同名已存在）：{Name}", skillInfo.Name);
            }
        }

        /// <summary>注销已注册技能（模块 Deactivate 时对称撤销内嵌技能）。</summary>
        public bool Unregister(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var removed = _catalog.TryUnregister(name);
            if (!removed)
            {
                // 文件通道技能由盘上文件决定，不能经注册通道注销
                _logger.LogDebug("注销未命中注册通道：{Name}", name);
            }

            return removed;
        }

        // ===== 禁用状态（写盘 + 让目录重扫；Task 11 迁移到 SkillWriteGate） =====

        /// <summary>
        /// 加载技能禁用状态。
        /// <para>目录每次重建快照都会自读两级 <c>skill-state.json</c>，故这里只需让目录重扫一次。</para>
        /// </summary>
        public async Task LoadSkillStateAsync(CancellationToken ct = default)
            => await _catalog.RefreshAsync(ct).ConfigureAwait(false);

        /// <summary>设置技能启用/禁用状态：写盘后让目录重扫，确保门面与目录同时生效。</summary>
        public async Task SetSkillEnabledAsync(string skillName, bool enabled, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(skillName))
                return;

            if (_directories is not null)
            {
                // 层级沿用目录上报的结果（项目级优先于用户级）
                var level = _catalog.Current.DisabledScopes.TryGetValue(skillName, out var scope)
                    ? scope
                    : ConfigLevel.User;

                var filePath = level == ConfigLevel.User
                    ? Path.Combine(_directories.UserSeeingDirectory, SkillStateFileName)
                    : Path.Combine(_directories.ProjectSeeingDirectory, SkillStateFileName);

                await WriteDisabledStateAsync(filePath, skillName, enabled, ct).ConfigureAwait(false);
            }

            await _catalog.RefreshAsync(ct).ConfigureAwait(false);
            OnSkillStateChanged?.Invoke();
        }

        // ===== 内部 =====

        /// <summary>把当前快照投影成名称 → 技能信息字典。</summary>
        private IReadOnlyDictionary<string, SkillInfo> Project(
            Func<SkillCatalogSnapshot, IEnumerable<SkillCatalogEntry>> selector)
        {
            var snapshot = _catalog.Current;
            var result = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in selector(snapshot))
                result[entry.Info.Name] = entry.Info;

            return result;
        }

        /// <summary>读-改-写某一级的禁用集合（串行化，避免并发写覆盖）。</summary>
        private async Task WriteDisabledStateAsync(
            string filePath,
            string skillName,
            bool enabled,
            CancellationToken ct)
        {
            await _stateWriteGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var disabled = await LoadDisabledSetAsync(filePath, ct).ConfigureAwait(false);
                if (enabled)
                    disabled.Remove(skillName);
                else
                    disabled.Add(skillName);

                await SaveDisabledSetAsync(filePath, disabled, ct).ConfigureAwait(false);
            }
            finally
            {
                _stateWriteGate.Release();
            }
        }

        /// <summary>从文件加载禁用名称集合。</summary>
        private async Task<HashSet<string>> LoadDisabledSetAsync(string filePath, CancellationToken ct)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(filePath))
                return result;

            try
            {
                var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<DisabledSkillsData>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

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
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // 状态文件损坏 / 不可读：按「无禁用项」处理，但必须可观测
                _logger.LogWarning(ex, "读取技能禁用状态失败，按空集合处理：{Path}", filePath);
            }

            return result;
        }

        /// <summary>保存禁用名称集合到文件。</summary>
        private async Task SaveDisabledSetAsync(string filePath, HashSet<string> disabledSkills, CancellationToken ct)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var data = new DisabledSkillsData { DisabledSkills = disabledSkills.ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
            _logger.LogInformation("技能禁用状态已保存：{Path} 数量={Count}", filePath, disabledSkills.Count);
        }

        /// <summary>禁用技能数据模型（沿用既有落盘格式）。</summary>
        private sealed class DisabledSkillsData
        {
            public List<string>? DisabledSkills { get; set; }
        }
    }
}
