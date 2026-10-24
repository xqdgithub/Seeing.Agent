using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Session.Core;

namespace Seeing.Agent.Skills.Commands;

/// <summary>
/// 动态技能命令与目录事件同步：技能出现即登记 <c>/&lt;name&gt;</c>，技能消失或被禁用即撤销。
/// <para>撤销判据是「本同步器登记过、但当前不可调用」——包含**被禁用**的技能（旧实现只按启用集合
/// 求差，禁用/删除的命令永远注销不掉）。撤销前校验命令类型，避免误删同名内置命令。</para>
/// <para>生命周期由 <see cref="SkillsModule"/> 的 Activate/Deactivate 拥有；回调线程可能并发，
/// 内部以锁串行化。</para>
/// </summary>
internal sealed class DynamicSkillCommandSync : IDisposable
{
    private readonly ISkillCatalog _catalog;
    private readonly ICommandRegistry _registry;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger _logger;

    /// <summary>本同步器登记过的命令名（撤销的输入集合）。</summary>
    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>同步闸：目录事件可能在 watcher / 命令执行线程触发。</summary>
    private readonly object _gate = new();

    private IDisposable? _subscription;
    private bool _disposed;

    public DynamicSkillCommandSync(
        ISkillCatalog catalog,
        ICommandRegistry registry,
        ISessionManager sessionManager,
        ILogger logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            foreach (var name in _registered)
                UnregisterIfOwned(name);

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
            foreach (var entry in snapshot.Entries)
            {
                if (!snapshot.DisabledNames.Contains(entry.Info.Name))
                    desired.Add(entry.Info.Name);
            }

            foreach (var name in _registered.Where(name => !desired.Contains(name)).ToList())
            {
                UnregisterIfOwned(name);
                _registered.Remove(name);
            }

            foreach (var name in desired.Where(name => !_registered.Contains(name)))
            {
                try
                {
                    _registry.Register(new DynamicSkillCommand(_sessionManager, name));
                    _registered.Add(name);
                }
                catch (Exception ex)
                {
                    // 单个命令登记失败不得中断其余同步，但必须可观测（下次事件会重试）
                    _logger.LogWarning(ex, "登记动态技能命令失败：{SkillName}", name);
                }
            }
        }
    }

    /// <summary>只撤销确实由本同步器登记的动态技能命令（同名内置命令不得被误删）。</summary>
    private void UnregisterIfOwned(string name)
    {
        try
        {
            if (_registry.GetCommand(name) is DynamicSkillCommand)
                _registry.Unregister(name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤销动态技能命令失败：{SkillName}", name);
        }
    }
}
