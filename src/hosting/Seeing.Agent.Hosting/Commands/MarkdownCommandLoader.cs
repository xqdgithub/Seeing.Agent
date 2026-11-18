using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Commands;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Hosting.Commands;

/// <summary>
/// 用户级 / 项目级 Markdown 命令（<c>~/.seeing/commands</c>、<c>{项目}/.seeing/commands</c>）的唯一登记方。
/// <para>
/// 为什么需要它：解析能力（<see cref="ICommandDiscovery.DiscoverFromMarkdownAsync"/>）早已存在，
/// 却没有任何调用方——命令面板里的「自定义命令」因此永远为空。
/// </para>
/// <para><b>生命周期只有两条</b>：</para>
/// <list type="number">
/// <item>首次登记：宿主命令初始化入口（<c>InitializeCommands()</c>）调用一次 <see cref="Reload"/>；</item>
/// <item>工作区切换 / 全量配置重载：由 <see cref="IReloadHandler"/> 驱动重扫（<b>不要</b>把这一步塞进
/// 进程级一次性初始化，否则第二次切换就不会生效）。</item>
/// </list>
/// <para><b>撤销按实例</b>（<see cref="ICommandRegistry.Unregister(ICommand)"/>），不会误删同名内置命令。</para>
/// <para><b>命名冲突</b>：与已有命令同名时跳过并告警，不静默覆盖（见 AGENTS.md「工具 ID 冲突静默覆盖」反模式）。</para>
/// </summary>
public sealed class MarkdownCommandLoader : IReloadHandler
{
    private readonly ICommandRegistry _registry;
    private readonly ICommandDiscovery _discovery;
    private readonly IWorkspaceProvider? _workspace;
    private readonly ISeeingDirectories? _directories;
    private readonly ILogger _logger;

    /// <summary>本加载器已登记的 Markdown 命令（名字 → 自己创建的实例，撤销的输入集合）。</summary>
    private readonly Dictionary<string, ICommand> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>重扫闸：重载编排器与宿主初始化可能并发触发。</summary>
    private readonly object _gate = new();

    /// <summary>创建 Markdown 命令加载器。</summary>
    public MarkdownCommandLoader(
        ICommandRegistry registry,
        ICommandDiscovery discovery,
        IWorkspaceProvider? workspace = null,
        ISeeingDirectories? directories = null,
        ILogger<MarkdownCommandLoader>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _workspace = workspace;
        _directories = directories;
        _logger = logger ?? NullLogger<MarkdownCommandLoader>.Instance;
    }

    /// <inheritdoc />
    public string ComponentId => "markdown-commands";

    /// <inheritdoc />
    public IReadOnlyList<Type> ChangeTypes { get; } = [typeof(WorkspaceChange), typeof(ConfigChange)];

    /// <inheritdoc />
    public Task ReloadAsync(IReloadSignal change, CancellationToken ct = default)
    {
        // 命令目录不对应任何配置节：只有「全量重载」才值得扫盘
        if (change is ConfigChange cfg && cfg.ChangedSections.Count > 0)
            return Task.CompletedTask;

        Reload(ct);
        return Task.CompletedTask;
    }

    /// <summary>扫描两个命令目录并重新登记（幂等）。返回本次生效的命令数。</summary>
    public int Reload(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var desired = Discover(ct);

            // 先按实例撤销上一轮：文件被删 / 正文改动都能生效（MarkdownCommand 持有模板正文）
            foreach (var command in _registered.Values)
                TryUnregister(command);

            _registered.Clear();

            var added = 0;
            var skipped = 0;
            foreach (var (name, command) in desired)
            {
                if (_registry.HasCommand(name))
                {
                    _logger.LogWarning(
                        "Markdown 命令与已有命令重名，已跳过：{CommandName}（{Source}）",
                        name,
                        command.Metadata.Source);
                    skipped++;
                    continue;
                }

                try
                {
                    _registry.Register(command);
                    _registered[name] = command;
                    added++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "登记 Markdown 命令失败：{CommandName}", name);
                }
            }

            if (added > 0 || skipped > 0)
            {
                _logger.LogInformation(
                    "Markdown 命令已加载：{Added} 条，跳过重名 {Skipped} 条",
                    added,
                    skipped);
            }

            return added;
        }
    }

    /// <summary>
    /// 扫描用户级 → 项目级目录；同名的项目级命令覆盖用户级（与技能目录的优先级一致）。
    /// <para>缺目录、解析失败都只记日志：一条坏命令不得影响其余命令登记。</para>
    /// </summary>
    private Dictionary<string, ICommand> Discover(CancellationToken ct)
    {
        var desired = new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateRoots())
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                // 命令目录很小（每文件一次读），这里同步取回：本方法同时服务同步初始化入口
                var commands = _discovery.DiscoverFromMarkdownAsync(root, ct).GetAwaiter().GetResult();
                foreach (var command in commands)
                    desired[command.Metadata.Name] = command;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "扫描 Markdown 命令目录失败，已跳过：{Directory}", root);
            }
        }

        return desired;
    }

    /// <summary>用户级在前、项目级在后（后者覆盖同名的前者的同名命令）。</summary>
    private IEnumerable<string> EnumerateRoots()
    {
        if (_directories is not null)
            yield return Path.Combine(_directories.UserSeeingDirectory, "commands");

        if (_workspace is { IsResolved: true })
            yield return Path.Combine(_workspace.GetProjectRoot(), ".seeing", "commands");
    }

    private void TryUnregister(ICommand command)
    {
        try
        {
            _registry.Unregister(command);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "撤销 Markdown 命令失败：{CommandName}", command.Metadata.Name);
        }
    }
}
