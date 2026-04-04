using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Hooks
{
    /// <summary>
    /// Hook 管理器接口
    /// </summary>
    public interface IHookManager
    {
        /// <summary>注册 Hook 处理器</summary>
        void RegisterHandler(IHookHandler handler);
        
        /// <summary>
        /// 触发 Hook
        /// </summary>
        /// <param name="hookPoint">Hook 点</param>
        /// <param name="input">输入数据（只读）</param>
        /// <param name="output">输出数据（可被 Hook 修改）</param>
        /// <param name="cancellationToken">取消令牌</param>
        Task<HookResult> TriggerAsync(
            string hookPoint, 
            Dictionary<string, object>? input = null, 
            Dictionary<string, object>? output = null,
            CancellationToken cancellationToken = default);
        
        /// <summary>移除 Hook 处理器</summary>
        bool RemoveHandler(string hookPoint, IHookHandler handler);
        
        /// <summary>清除指定 Hook 点的所有处理器</summary>
        bool ClearHandlers(string hookPoint);
        
        /// <summary>获取指定 Hook 点的处理器数量</summary>
        int GetHandlerCount(string hookPoint);
    }
}