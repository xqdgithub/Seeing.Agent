namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// <c>skill-state.json</c> 读写闸：以 <see cref="SemaphoreSlim"/> 串行化「读-改-写」全过程。
/// <para>禁用状态是整文件覆盖写；不串行化时并发开关会互相覆盖（后写者丢掉前写者的名字），
/// 表现为「界面上点了禁用但重进还是启用」的静默丢更新。闸只保护状态文件，不参与扫盘。</para>
/// <para>读侧（技能目录刷新）与写侧共用同一实例：目录刷新可能发生在写者已持闸的逻辑流程内
/// （写完后立即刷新），因此闸对**同一条异步流**可重入——否则会自重入死锁。重入只发生在
/// 同一个逻辑流程内，其它流程仍被排它。</para>
/// </summary>
public sealed class SkillWriteGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前异步流持有深度（&gt;0 表示该流程已持闸，重入直接执行）。</summary>
    private readonly AsyncLocal<int> _heldDepth = new();

    /// <summary>在闸内执行一次完整的读-改-写。</summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="action">动作（参数为取消令牌）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>动作结果。</returns>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 同一异步流已持闸（写者 → 目录刷新 → 读状态文件）：直接执行，不重复获取。
        if (_heldDepth.Value > 0)
            return await action(ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _heldDepth.Value++;
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _heldDepth.Value--;
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
