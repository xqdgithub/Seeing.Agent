namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// <c>skill-state.json</c> 读写闸：以 <see cref="SemaphoreSlim"/> 串行化「读-改-写」全过程。
/// <para>禁用状态是整文件覆盖写；不串行化时并发开关会互相覆盖（后写者丢掉前写者的名字），
/// 表现为「界面上点了禁用但重进还是启用」的静默丢更新。闸只保护状态文件，不参与扫盘。</para>
/// <para><b>全局锁序（N1，单向不可反转）</b>：状态写闸 →（释放）→ 目录扫盘闸。
/// 写入方（<c>SkillWriter</c>）在闸内只做状态文件读写，释放后才刷新目录；
/// 目录方（<c>SkillCatalogCore</c>）先取本闸读禁用状态、释放后再取目录扫盘闸。
/// 因此不存在「一方持写闸等目录闸、另一方持目录闸等写闸」的互等。</para>
/// <para><b>不重入</b>：既有两个调用点（<c>SkillWriter.SetEnabledAsync</c>、
/// <c>SkillCatalogCore.LoadDisabledStateAsync</c>）都是顶层调用，锁序修复后不存在嵌套。
/// 此前用 <c>AsyncLocal</c> 放行重入还有一个隐患：它会流入闸内启动的子任务，
/// 让子任务继承「已持闸」并绕过信号量，在临界区内并发改状态文件。因此不再提供重入。</para>
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
