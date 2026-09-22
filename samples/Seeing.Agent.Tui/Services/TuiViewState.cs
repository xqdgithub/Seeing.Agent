using Seeing.Session.Core;

namespace Seeing.Agent.Tui.Services;

/// <summary>
/// TUI 会话视图态（纯数据，单活跃会话）。由快照重建 + 事件增量 upsert 维护。
/// <para>
/// 线程安全：内部一把锁保护 <c>_blocks</c>/<c>_index</c>/<see cref="Revision"/>；
/// <see cref="Blocks"/>/<see cref="Tools"/> 读取「不可变快照数组」字段（原子替换，读取无需加锁），
/// 故渲染线程枚举期间即使 taskpool 线程并发 <see cref="Upsert"/>，也不会抛
/// <see cref="InvalidOperationException"/>，仅观察到变更前/后的完整视图。
/// </para>
/// <para>
/// 约定一：块的<strong>集合字段</strong>（如 <see cref="TuiToolState.Steps"/>）禁止原地变异，
/// 必须构造新对象并经 <see cref="Upsert"/> 或 <see cref="Update"/> 发布，避免枚举期间的撕裂读。
/// </para>
/// <para>
/// 约定二：一个 <c>(LoopId, Step)</c> 只对应一个 assistant 块，Key 由 <see cref="AssistantKey"/> 决定；
/// 快照重建与事件解释器必须使用同一规则（<c>step{step}</c>）。
/// </para>
/// <para>
/// 约定三：块的<strong>标量字段</strong>（<c>IsTerminal</c>/<c>IsStreaming</c>/<c>UpdatedAt</c> 等）允许
/// 由事件线程原地改写：bool/引用读写不存在撕裂，最坏只观察到一次旧值并在下个事件自愈；
/// 不要为这些字段引入跨线程锁，以免渲染线程与事件线程互相阻塞。
/// </para>
/// </summary>
public sealed class TuiViewState
{
    private readonly object _gate = new();
    private readonly List<TuiBlock> _blocks = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    // 不可变快照：元素与 _blocks 为同一引用；只需原子替换，读取方无需加锁。
    private volatile TuiBlock[] _snapshot = [];

    public required string SessionId { get; init; }
    public string? Title { get; set; }
    public string? AgentId { get; set; }
    public string? ModelId { get; set; }
    public string? ThinkingEffort { get; set; }
    public string? Scenario { get; set; }
    public string? AcpMode { get; set; }
    public string? ActiveExecutionId { get; set; }
    public bool IsExecuting { get; set; }
    public int QueueLength { get; set; }
    public string? LastError { get; set; }
    public IReadOnlyList<TuiTodo> Todos { get; set; } = [];
    public TuiBudget? Budget { get; set; }
    public long Revision { get; private set; }

    public IReadOnlyList<TuiBlock> Blocks => _snapshot;

    public IEnumerable<TuiBlock> Tools => _snapshot.Where(b => b.Kind == TuiBlockKind.Tool);

    public void Touch()
    {
        lock (_gate)
        {
            Revision++;
            _snapshot = _blocks.ToArray();
        }
    }

    public TuiBlock? Find(string key)
    {
        lock (_gate)
            return _index.TryGetValue(key, out var i) ? _blocks[i] : null;
    }

    public TuiBlock Upsert(TuiBlock block)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(block.Key, out var i))
            {
                var existing = _blocks[i];
                existing.Text = block.Text;
                existing.Reasoning = block.Reasoning;
                existing.Title = block.Title;
                existing.IsStreaming = block.IsStreaming;
                existing.IsCancelled = block.IsCancelled;
                existing.IsTerminal = block.IsTerminal;
                existing.Tool = block.Tool ?? existing.Tool;
                existing.UpdatedAt = block.UpdatedAt;
                Revision++;
                _snapshot = _blocks.ToArray();
                return existing;
            }

            _index[block.Key] = _blocks.Count;
            _blocks.Add(block);
            Revision++;
            _snapshot = _blocks.ToArray();
            return block;
        }
    }

    /// <summary>
    /// 在锁内按 Key 定位块并执行变异（供同组 tracker/interpreter 等使用），随后重建快照并推进 <see cref="Revision"/>。
    /// 找不到 Key 时静默返回。
    /// </summary>
    public void Update(string key, Action<TuiBlock> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var i))
                return;

            mutate(_blocks[i]);
            Revision++;
            _snapshot = _blocks.ToArray();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var i))
                return;

            _blocks.RemoveAt(i);
            RebuildIndex();
            Revision++;
            _snapshot = _blocks.ToArray();
        }
    }

    public void ResetFromSession(SessionData session)
    {
        lock (_gate)
        {
            _blocks.Clear();
            _index.Clear();

            Title = session.Title;
            AgentId = session.SelectedAgent;
            ModelId = session.SelectedModel;
            ThinkingEffort = session.SelectedThinkingEffort;
            Scenario = session.Scenario;
            AcpMode = session.SelectedAcpMode;

            foreach (var msg in session.Messages)
            {
                if (string.Equals(msg.Role, MessageRole.Tool, StringComparison.Ordinal))
                    continue;

                if (msg.IsSummary)
                {
                    Add(new TuiBlock
                    {
                        Key = $"compaction:{msg.Id ?? Guid.NewGuid().ToString("N")}",
                        Kind = TuiBlockKind.Compaction,
                        Text = msg.Content,
                        IsTerminal = true,
                    });
                    continue;
                }

                if (string.Equals(msg.Role, MessageRole.User, StringComparison.Ordinal))
                {
                    Add(new TuiBlock
                    {
                        Key = $"user:{msg.Id ?? Guid.NewGuid().ToString("N")}",
                        Kind = TuiBlockKind.User,
                        Text = msg.Content,
                        IsTerminal = true,
                    });
                    continue;
                }

                if (string.Equals(msg.Role, MessageRole.Assistant, StringComparison.Ordinal))
                {
                    // 与 TuiEventInterpreter 对齐：一个 (LoopId, Step) 只对应一个 assistant 块。
                    // LoopId 为空时用 step{Step}（而非消息 Id），避免同一消息产生两个块。
                    var key = AssistantKey(msg.LoopId, msg.Step, $"step{msg.Step}");
                    Add(new TuiBlock
                    {
                        Key = key,
                        Kind = TuiBlockKind.Assistant,
                        LoopId = msg.LoopId,
                        Step = msg.Step,
                        Text = msg.Content,
                        Reasoning = msg.ReasoningContent ?? string.Empty,
                        IsTerminal = true,
                    });

                    if (msg.ToolCalls is { Count: > 0 })
                    {
                        foreach (var call in msg.ToolCalls)
                            Add(BuildToolBlock(call));
                    }
                    continue;
                }

                if (string.Equals(msg.Role, MessageRole.System, StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(msg.Content))
                        continue;

                    Add(new TuiBlock
                    {
                        Key = $"sys:{msg.Id ?? Guid.NewGuid().ToString("N")}",
                        Kind = TuiBlockKind.System,
                        Text = msg.Content,
                        IsTerminal = true,
                    });
                }
            }

            Revision++;
            _snapshot = _blocks.ToArray();
        }
    }

    public static string AssistantKey(string? loopId, int step, string? messageId)
        => string.IsNullOrEmpty(loopId)
            ? $"asst:{messageId ?? "anon"}"
            : $"{loopId}_step{step}";

    public static TuiBlock BuildToolBlock(SessionToolCall call)
    {
        var tool = new TuiToolState
        {
            CallId = call.Id,
            Name = call.Name,
            Status = MapStatus(call.Status),
            Arguments = call.Arguments,
            Output = call.Result,
            Error = call.Error,
            Title = call.Title,
            TaskId = call.TaskId,
            TaskAgent = call.TaskAgent,
            TaskDescription = call.TaskDescription,
        };

        if (call.TaskSteps is { Count: > 0 })
        {
            foreach (var step in call.TaskSteps)
                tool.Steps.Add(new TuiTaskStep(step.ToolName ?? string.Empty, step.Preview ?? string.Empty, MapStatus(step.Status)));
        }

        return new TuiBlock
        {
            Key = $"tool:{call.Id}",
            Kind = TuiBlockKind.Tool,
            Tool = tool,
            IsTerminal = tool.Status is not (TuiToolStatus.Pending or TuiToolStatus.Running),
        };
    }

    public static TuiToolStatus MapStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "pending" => TuiToolStatus.Pending,
        "running" => TuiToolStatus.Running,
        "success" or "completed" or "complete" => TuiToolStatus.Success,
        "failed" or "failure" or "error" => TuiToolStatus.Failed,
        "rejected" => TuiToolStatus.Rejected,
        "cancelled" or "canceled" => TuiToolStatus.Cancelled,
        _ => TuiToolStatus.Pending,
    };

    /// <summary>仅在已持锁时调用：按 Key 去重追加（同 Key 后者覆盖前者），并登记索引。</summary>
    private void Add(TuiBlock block)
    {
        if (_index.TryGetValue(block.Key, out var i))
        {
            _blocks[i] = block;
            return;
        }

        _index[block.Key] = _blocks.Count;
        _blocks.Add(block);
    }

    /// <summary>仅在已持锁时调用：按当前 <c>_blocks</c> 全量重建 Key 索引。</summary>
    private void RebuildIndex()
    {
        _index.Clear();
        for (var i = 0; i < _blocks.Count; i++)
            _index[_blocks[i].Key] = i;
    }
}
