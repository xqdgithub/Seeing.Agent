using Seeing.Agent.Abstractions.Skills;

namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// <see cref="ISkillContributionRegistry"/> 的唯一实现：登记贡献源，加载期按键回源。
/// </summary>
public sealed class SkillContributionRegistry : ISkillContributionRegistry
{
    private readonly object _gate = new();
    private readonly List<ISkillContributionSource> _sources = [];

    /// <summary>源集合变更时通知（目录据此重合并贡献通道）。</summary>
    public event Func<CancellationToken, Task>? Changed;

    /// <inheritdoc />
    public IReadOnlyList<ISkillContributionSource> Sources
    {
        get
        {
            lock (_gate)
                return _sources.ToArray();
        }
    }

    /// <inheritdoc />
    public void Register(ISkillContributionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (_sources.Contains(source))
                return;

            _sources.Add(source);
        }

        RaiseChanged();
    }

    /// <inheritdoc />
    public void Unregister(ISkillContributionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_sources.Remove(source))
                return;
        }

        RaiseChanged();
    }

    /// <inheritdoc />
    public async Task<SkillContributionContent?> LoadAsync(
        string key,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        foreach (var source in Sources)
        {
            ct.ThrowIfCancellationRequested();
            var content = await source.LoadAsync(key, ct).ConfigureAwait(false);
            if (content is not null)
                return content;
        }

        return null;
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        // 目录合并是异步的：登记线程不得阻塞；失败由订阅方自行记日志
        _ = Task.Run(async () =>
        {
            try
            {
                await handlers(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 订阅方应自捕；此处兜底避免未观察任务异常
            }
        });
    }
}
