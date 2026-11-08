namespace Seeing.Agent.Comms;

/// <summary>
/// 通联子系统里<b>随工作区走</b>的进程内状态。
/// <para><b>为什么需要这个端口</b>：目录条目在文件里（路径由工作区推导），但回复锚点、
/// 频控滑窗这类状态只活在内存中，且都描述「旧工作区里发生过什么」。工作区切换后，
/// 这些状态必须由<b>持有者自己</b>清掉——让切换编排方去逐类型认字段，
/// 等于把每个实现的内部结构暴露给外层（新增一处状态就改一次外层）。</para>
/// <para>这是 Comms 包的内部生命周期缝隙，不进 <c>Abstractions</c>：
/// 它只服务于本能力的工作区切换参与者，不是给其它包实现的契约。</para>
/// </summary>
public interface ICommsWorkspaceScopedState
{
    /// <summary>按当前工作区重建状态（实现须自行解析新的项目路径）。</summary>
    void ResetForNewWorkspace();
}
