namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// <c>skill-state.json</c> 写闸：以 <see cref="SemaphoreSlim"/> 串行化「读-改-写」全过程。
/// <para>禁用状态是整文件覆盖写；不串行化时并发开关会互相覆盖（后写者丢掉前写者的名字），
/// 表现为「界面上点了禁用但重进还是启用」的静默丢更新。闸只保护状态文件，不参与扫盘。</para>
/// </summary>
public sealed class SkillWriteGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>在闸内执行一次完整的读-改-写。</summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="action">动作（参数为取消令牌）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>动作结果。</returns>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
