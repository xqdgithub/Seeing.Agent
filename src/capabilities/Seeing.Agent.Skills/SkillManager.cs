using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;

namespace Seeing.Agent.Skills
{
    /// <summary>
    /// 技能只读门面 — 全部答案来自 <see cref="ISkillCatalog"/> 的当前快照，自身不持有技能字典、
    /// 不扫盘、不解析 frontmatter、不写 <c>skill-state.json</c>。
    /// <para>
    /// 技能是上下文提供者，不是可执行单元。技能内容通过 SkillTool 注入到 LLM 上下文中。
    /// </para>
    /// <para>
    /// 本类**不再**实现 <c>IPromptSectionContributor</c>：<c>## Skills</c> 区块由上下文源
    /// <c>SkillsContextSource</c> 的基线整体拥有，避免同名标题出现两次。
    /// </para>
    /// </summary>
    public class SkillManager : ISkillManager
    {
        private readonly ILogger<SkillManager> _logger;
        private readonly SkillCatalogCore _catalog;

        /// <summary>
        /// 创建门面。
        /// </summary>
        /// <param name="logger">门面日志。</param>
        /// <param name="catalog">技能目录（唯一真相源）。</param>
        public SkillManager(
            ILogger<SkillManager> logger,
            SkillCatalogCore catalog)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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
            return entry is null ? [] : GetSkillFiles(entry, limit);
        }

        /// <summary>
        /// 按目录条目取技能目录下的相关文件（排除 SKILL.md）。
        /// <para>目录来自 <see cref="SkillCatalogEntry.DirectoryPath"/>（构建快照时写入的数据字段），
        /// 因此执行级钉住的旧条目同样可用，不依赖 <see cref="SkillInfo.Location"/>。</para>
        /// </summary>
        public List<string> GetSkillFiles(SkillCatalogEntry entry, int limit = 10)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (entry.Source == SkillSource.Registered
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
                _logger.LogWarning(ex, "获取技能文件列表失败：{Skill} 目录={Directory}", entry.Info.Name, entry.DirectoryPath);
                return [];
            }
        }

        // ===== 写面（编译期拦截：唯一写入口是 ISkillCatalog） =====

        /// <summary>
        /// 添加技能搜索目录。
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// 根列表是目录的唯一事实（由 SkillLoader 一次算出），任何「追加目录」都会造成第二份根定义。
        /// </exception>
        [Obsolete("请使用 ISkillCatalog", error: true)]
        public void AddSearchDirectory(string directory)
            => throw new NotSupportedException(
                "技能搜索目录不再支持逐个追加：根列表由 SkillLoader 统一计算后经 ISkillCatalog.SetRoots 写入。" +
                "请改用 ISkillCatalog，或在 SkillsOptions.Paths 中配置技能目录。");

        /// <summary>重新扫描并发布目录快照。</summary>
        [Obsolete("请使用 ISkillCatalog", error: true)]
        public Task DiscoverSkillsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "请改用 ISkillCatalog.RefreshAsync（强制重建）或 ISkillCatalog.EnsureFreshAsync（指纹未变时复用）。");

        /// <summary>刷新技能列表（转发到 <see cref="ISkillCatalog.RefreshAsync"/>）。</summary>
        public async Task RefreshSkillsAsync(CancellationToken cancellationToken = default)
            => await _catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);

        /// <summary>注册内嵌技能（模块 EmbeddedResource）。</summary>
        [Obsolete("请使用 ISkillCatalog", error: true)]
        public void RegisterEmbeddedSkill(SkillInfo skillInfo)
            => throw new NotSupportedException(
                "请改用 ISkillCatalog.RegisterAsync(info, providerId)：注册通道必须显式给出提供方，且需要 await。");

        /// <summary>注销已注册技能（模块 Deactivate 时对称撤销内嵌技能）。</summary>
        [Obsolete("请使用 ISkillCatalog", error: true)]
        public bool Unregister(string name)
            => throw new NotSupportedException("请改用 ISkillCatalog.UnregisterAsync(name)。");

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
    }
}
