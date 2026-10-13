using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Runtime;
using Seeing.Session.Core;
using Seeing.Session.Persistence;

namespace Seeing.Agent.Hosting.Execution;

/// <summary>
/// 启动扫描归档服务：启动时扫描一次，按组整体归档不活跃会话（含子 / 分支 / 交接）。
/// <para>不阻塞 Host 启动主流程（<see cref="BackgroundService"/>）；执行中的组保守跳过。</para>
/// </summary>
public sealed class SessionArchivalScanService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly SessionArchivalOptions _options;
    private readonly ILogger<SessionArchivalScanService> _logger;

    public SessionArchivalScanService(
        IServiceProvider services,
        SessionArchivalOptions options,
        ILogger<SessionArchivalScanService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ScanOnStartup)
        {
            _logger.LogDebug("会话启动扫描归档未启用");
            return;
        }

        try
        {
            // 让 Host 完成启动主流程后再扫描
            await Task.Yield();

            var groupManager = _services.GetRequiredService<ISessionGroupManager>();
            var sessionManager = _services.GetRequiredService<ISessionManager>();
            var runtime = _services.GetService<ISessionRuntime>();

            var cutoff = DateTime.Now - _options.InactivityThreshold;

            // 会话摘要索引：Id → (LastActiveAt, MessageCount)（活跃度/规模真相来源）
            var lastActive = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            var messageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var archivedIds = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var summary in sessionManager.ListSummariesAsync(stoppingToken).ConfigureAwait(false))
            {
                lastActive[summary.Id] = summary.LastActiveAt;
                messageCounts[summary.Id] = summary.MessageCount;
                if (summary.IsArchived)
                    archivedIds.Add(summary.Id);
            }

            var archivedGroups = 0;
            var archivedLoose = 0;
            var groupedSessionIds = new HashSet<string>(StringComparer.Ordinal);

            await foreach (var group in groupManager.ListGroupsAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var member in group.Members)
                    groupedSessionIds.Add(member.SessionId);

                // 组级判定：全部成员都不活跃（LastActiveAt 早于阈值）才归档；任一成员活跃即跳过
                var allInactive = group.Members.Count > 0;
                foreach (var member in group.Members)
                {
                    if (!lastActive.TryGetValue(member.SessionId, out var active))
                    {
                        // 摘要缺失（可能已归档）→ 不阻塞判定
                        continue;
                    }

                    if (active >= cutoff)
                    {
                        allInactive = false;
                        break;
                    }
                }

                if (!allInactive)
                    continue;

                // 消息数下限：组内所有成员均低于下限则跳过（避免归档空会话）
                if (_options.MinMessageCount > 0
                    && group.Members.All(m =>
                        !messageCounts.TryGetValue(m.SessionId, out var c) || c < _options.MinMessageCount))
                {
                    continue;
                }

                if (IsGroupExecuting(group, runtime))
                {
                    _logger.LogDebug("跳过执行中的会话组: GroupId={GroupId}", group.Id);
                    continue;
                }

                try
                {
                    if (await groupManager.ArchiveGroupAsync(group.Id, stoppingToken).ConfigureAwait(false))
                        archivedGroups++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "归档会话组失败: GroupId={GroupId}", group.Id);
                }
            }

            // 无组独立会话
            foreach (var (sessionId, active) in lastActive)
            {
                if (groupedSessionIds.Contains(sessionId) || archivedIds.Contains(sessionId))
                    continue;
                if (active >= cutoff)
                    continue;
                if (_options.MinMessageCount > 0
                    && messageCounts.TryGetValue(sessionId, out var count)
                    && count < _options.MinMessageCount)
                    continue;
                if (runtime?.GetOverview(sessionId).HasActiveExecution == true)
                    continue;

                try
                {
                    if (await sessionManager.ArchiveAsync(sessionId, stoppingToken).ConfigureAwait(false))
                        archivedLoose++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "归档独立会话失败: SessionId={SessionId}", sessionId);
                }
            }

            _logger.LogInformation(
                "启动扫描归档完成: Groups={Groups}, Loose={Loose}, Threshold={Threshold}",
                archivedGroups, archivedLoose, _options.InactivityThreshold);
        }
        catch (OperationCanceledException)
        {
            // 启动即取消，忽略
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动扫描归档失败");
        }
    }

    private static bool IsGroupExecuting(SessionGroup group, ISessionRuntime? runtime)
    {
        if (runtime == null)
            return false;

        foreach (var member in group.Members)
        {
            if (runtime.GetOverview(member.SessionId).HasActiveExecution)
                return true;
        }

        return false;
    }
}
