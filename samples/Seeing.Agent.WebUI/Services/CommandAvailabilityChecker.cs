using Seeing.Agent.WebUI.State;

namespace Seeing.Agent.WebUI.Services
{
    /// <summary>
    /// 命令可用性检查器 - 根据会话状态判断命令是否可执行
    /// </summary>
    public class CommandAvailabilityChecker
    {
        /// <summary>
        /// 检查命令是否可执行
        /// </summary>
        /// <param name="commandName">命令名称（不含斜杠）</param>
        /// <param name="session">会话状态</param>
        /// <returns>是否可执行</returns>
        public bool CanExecute(string commandName, SessionState? session)
        {
            if (session == null)
                return true;

            return commandName.ToLowerInvariant() switch
            {
                "undo" => HasUserMessages(session),
                "redo" => HasRevertState(session),
                "clear" => HasMessages(session),
                "compact" => HasMessages(session),
                "share" => HasActiveSession(session),
                "fork" => HasActiveSession(session),
                _ => true
            };
        }

        /// <summary>
        /// 获取禁用原因
        /// </summary>
        /// <param name="commandName">命令名称（不含斜杠）</param>
        /// <param name="session">会话状态</param>
        /// <returns>禁用原因，如果可用则返回 null</returns>
        public string? GetDisabledReason(string commandName, SessionState? session)
        {
            if (session == null)
                return null;

            return commandName.ToLowerInvariant() switch
            {
                "undo" when !HasUserMessages(session) => "没有可撤销的消息",
                "redo" when !HasRevertState(session) => "没有可重做的操作",
                "clear" when !HasMessages(session) => "会话为空",
                "compact" when !HasMessages(session) => "会话为空",
                "share" when !HasActiveSession(session) => "没有活动会话",
                "fork" when !HasActiveSession(session) => "没有活动会话",
                _ => null
            };
        }

        private static bool HasUserMessages(SessionState session)
        {
            return session.Messages?.Any(m => m.Role == "user") ?? false;
        }

        private static bool HasMessages(SessionState session)
        {
            return session.Messages?.Any() ?? false;
        }

        private static bool HasRevertState(SessionState session)
        {
            // 检查是否有可重做的状态
            // 当前 SessionData 没有跟踪 revert 状态，暂时返回 false
            // TODO: 当 SessionData 支持 revert 跟踪时更新此逻辑
            return false;
        }

        private static bool HasActiveSession(SessionState session)
        {
            return session.CurrentSession != null;
        }
    }
}
