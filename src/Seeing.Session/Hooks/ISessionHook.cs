using System.Threading.Tasks;

namespace Seeing.Session.Hooks
{
    /// <summary>
    /// Session Hook 处理器接口
    /// </summary>
    public interface ISessionHook
    {
        /// <summary>Hook 点</summary>
        string HookPoint { get; }
        
        /// <summary>优先级（越小越先执行）</summary>
        int Priority { get; }
        
        /// <summary>执行 Hook</summary>
        Task<SessionHookResult> ExecuteAsync(SessionHookContext context);
    }
}