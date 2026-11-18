using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Skills.Catalog;
using Seeing.Session.Core;

namespace Seeing.Agent.Skills.Commands;

/// <summary>
/// 动态技能命令与目录事件同步：技能出现即登记 <c>/&lt;name&gt;</c>，技能消失或被禁用即撤销。
/// <para><b>按实例撤销</b>：本同步器保存自己创建的 <see cref="DynamicSkillCommand"/>，
/// 撤销时按实例定位。同名命令可能同时存在于另一个 Runtime（ACP 透传命令），
/// 按名撤销会随机删掉别家的那份。</para>
/// <para><b>按 Runtime 判重</b>：只有 Native 下已有同名命令才跳过，
/// 别的 Runtime 有同名命令不影响本次登记（否则 ACP 一开，Native 技能命令会全部被误判为重名）。</para>
/// <para>生命周期由 <see cref="SkillsModule"/> 的 Activate/Deactivate 拥有；回调线程可能并发，
/// 内部以锁串行化。</para>
/// </summary>
internal sealed class DynamicSkillCommandSync : IDisposable
{
    private readonly ISkillCatalog _catalog;
    private readonly ICommandRegistry _registry;
    private readonly ISessionManager _sessionManager;
    private readonly ISkillContributionRegistry? _contributions;
    private readonly ILogger _logger;

    /// <summary>本同步器登记过的命令（名字 → 自己创建的实例；撤销的输入集合）。</summary>
    private readonly Dictionary<string, ICommand> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同步闸：目录事件可能在 watcher / 命令执行线程触发。</summary>
    private readonly object _gate = new();

    private IDisposable? _subscription;
    private bool _disposed;

    public DynamicSkillCommandSync(
        ISkillCatalog catalog,
        ICommandRegistry registry,
        ISessionManager sessionManager,
        ILogger logger,
        ISkillContributionRegistry? contributions = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _contributions = contributions;
    }

    /// <summary>订阅目录变更并按当前快照对齐一次（幂等）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _subscription is not null)
                return;

            _subscription = _catalog.Subscribe(Apply);
            Apply(_catalog.Current);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _subscription?.Dispose();
            _subscription = null;

            foreach (var command in _registered.Values)
                UnregisterOwned(command);

            _registered.Clear();
        }
    }

    /// <summary>对齐命令集合：注销不可用项、登记新可用项。</summary>
    private void Apply(SkillCatalogSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in SkillCatalogProjection.EnabledEntries(snapshot))
                desired.Add(entry.Info.Name);

            var removed = 0;
            foreach (var name in _registered.Keys.Where(name => !desired.Contains(name)).ToList())
            {
                if (UnregisterOwned(_registered[name]))
                    removed++;

                _registered.Remove(name);
            }

            var added = 0;
            var skipped = 0;
            foreach (var name in desired.Where(name => !_registered.ContainsKey(name)))
            {
                // 只看 Native 这一组：同名命令可能已由 ACP 模块登记在 AcpPassthrough 下，
                // 那不代表 Native 面板里已经有这个名字。
                if (_registry.GetCommand(name, AgentRuntime.Native) is not null)
                {
                    _logger.LogWarning(
                        "动态技能命令与已有命令重名，已跳过：{SkillName}",
                        name);
                    skipped++;
                    continue;
                }

                var command = new DynamicSkillCommand(_sessionManager, name, _contributions);
                try
                {
                    _registry.Register(command);
                    _registered[name] = command;
                    added++;
                }
                catch (Exception ex)
                {
                    // 单个命令登记失败不得中断其余同步，但必须可观测（下次事件会重试）
                    _logger.LogWarning(ex, "登记动态技能命令失败：{SkillName}", name);
                }
            }

            // 每次对齐都留一行账：面板里看不到技能命令时，据此可判断是「目录没有」
            // 还是「登记被跳过」（此前只有失败才记日志，成功路径完全静默）。
            _logger.LogInformation(
                "技能命令同步：可用={Desired}, 新增={Added}, 撤销={Removed}, 跳过重名={Skipped}, 累计持有={Owned}",
                desired.Count,
                added,
                removed,
                skipped,
                _registered.Count);
        }
    }

    /// <summary>撤销本同步器登记的那一个实例（同名内置命令 / 其它 Runtime 的同名命令不受影响）。</summary>
    private bool UnregisterOwned(ICommand command)
    {
        try
        {
            return _registry.Unregister(command);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤销动态技能命令失败：{SkillName}", command.Metadata.Name);
            return false;
        }
    }
}
