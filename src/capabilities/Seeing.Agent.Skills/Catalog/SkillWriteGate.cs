namespace Seeing.Agent.Skills.Catalog;

/// <summary>
/// <c>skill-state.json</c> 读写闸：以 <see cref="SemaphoreSlim"/> 串行化「读-改-写」全过程。
/// <para>禁用状态是整文件覆盖写；不串行化时并发开关会互相覆盖（后写者丢掉前写者的名字），
/// 表现为「界面上点了禁用但重进还是启用」的静默丢更新。闸只保护状态文件，不参与扫盘。</para>
/// <para><b>全局锁序（N1，单向不可反转）</b>：状态写闸 →（释放）→ 目录扫盘闸。
/// 写入方（<c>SkillWriter</c>）在闸内只做状态文件读写，释放后才刷新目录；
/// 目录方（<c>SkillCatalogCore</c>）先取本闸读禁用状态、释放后再取目录扫盘闸。
/// 因此不存在「一方持写闸等目录闸、另一方持目录闸等写闸」的互等。</para>
/// <para>读侧与写侧共用同一实例；<b>同一条异步流</b>可重入（<see cref="AsyncLocal{T}"/>），
/// 该能力仅为兼容既有调用点保留——锁序修复后正常路径已不再嵌套，新代码不得依赖它。</para>
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
