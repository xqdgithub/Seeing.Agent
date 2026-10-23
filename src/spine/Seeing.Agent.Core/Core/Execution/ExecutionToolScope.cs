using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Core.Execution;

/// <summary>
/// 执行级工具可见性作用域实现：由执行器按执行创建，种子来自本轮结算视图。
/// <para>
/// 可调用集 = inline ∪ 本执行已加载；加载只接受本轮结算中的 deferred 工具，
/// 因此不会绕过 Agent 白/黑名单或模块过滤。
/// </para>
/// <para>工具调用在同一步内并行执行，所有读写都在同一把锁内完成。</para>
/// </summary>
internal sealed class ExecutionToolScope : IExecutionToolScope
{
    private readonly object _gate = new();
    private readonly HashSet<string> _inline = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deferred = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loaded = new();
    private readonly ExposedTool[] _deferredTools;

    public ExecutionToolScope(SessionToolSchemaResolution? settlement)
    {
        if (settlement is null)
        {
            _deferredTools = [];
            return;
        }

        _deferredTools = settlement.Tools
            .Where(t => t.Mode == ToolExposureMode.Deferred)
            .ToArray();

        foreach (var tool in settlement.Tools)
        {
            switch (tool.Mode)
            {
                case ToolExposureMode.Inline:
                    _inline.Add(tool.Id);
                    break;
                case ToolExposureMode.Deferred:
                    _deferred.Add(tool.Id);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExposedTool> DeferredTools => _deferredTools;

    /// <inheritdoc />
    public IReadOnlyList<string> LoadedIds
    {
        get
        {
            lock (_gate)
                return _loaded.ToArray();
        }
    }

    /// <inheritdoc />
    public bool IsCallable(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
            return false;

        lock (_gate)
            return _inline.Contains(toolId) || _loaded.Contains(toolId, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool IsDeferred(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
            return false;

        lock (_gate)
            return _deferred.Contains(toolId);
    }

    /// <inheritdoc />
    public int TryLoad(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);

        lock (_gate)
        {
            var added = 0;
            foreach (var raw in toolIds)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var id = raw.Trim();

                // 只有本轮结算判定为延迟暴露的工具才允许加载；未知/已内联一律忽略。
                if (!_deferred.Contains(id) || _inline.Contains(id))
                    continue;

                if (_loaded.Contains(id, StringComparer.OrdinalIgnoreCase))
                    continue;

                _loaded.Add(id);
                added++;
            }

            return added;
        }
    }
}
