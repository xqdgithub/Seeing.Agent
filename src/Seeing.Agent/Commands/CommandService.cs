using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Hooks;
using System.Text.Json;

namespace Seeing.Agent.Commands
{
    /// <summary>
    /// 命令执行上下文
    /// </summary>
    public class CommandContext
    {
        /// <summary>命令名称</summary>
        public string CommandName { get; set; } = string.Empty;
        
        /// <summary>会话 ID</summary>
        public string SessionId { get; set; } = string.Empty;
        
        /// <summary>消息 ID（可选）</summary>
        public string? MessageId { get; set; }
        
        /// <summary>命令参数</summary>
        public string Arguments { get; set; } = string.Empty;
        
        /// <summary>取消令牌</summary>
        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// 命令执行结果
    /// </summary>
    public class CommandResult
    {
        /// <summary>是否成功</summary>
        public bool Success { get; set; }
        
        /// <summary>命令输出</summary>
        public string Output { get; set; } = string.Empty;
        
        /// <summary>错误信息</summary>
        public string? Error { get; set; }
        
        /// <summary>附加数据</summary>
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// 命令执行服务接口 - 用于执行自定义命令并触发相关 Hook
    /// </summary>
    public interface ICommandService
    {
        /// <summary>
        /// 执行命令（触发 command.execute.before Hook）
        /// </summary>
        /// <param name="context">命令上下文</param>
        /// <param name="executor">实际执行器</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>命令执行结果</returns>
        Task<CommandResult> ExecuteAsync(
            CommandContext context,
            Func<CommandContext, CancellationToken, Task<CommandResult>> executor,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 命令执行服务实现 - 通过 Hook 机制允许在命令执行前进行拦截和修改
    /// </summary>
    public class CommandService : ICommandService
    {
        private readonly ILogger<CommandService> _logger;
        private readonly IHookManager _hookManager;

        public CommandService(
            ILogger<CommandService> logger,
            IHookManager hookManager)
        {
            _logger = logger;
            _hookManager = hookManager;
        }

        /// <summary>
        /// 执行命令（触发 command.execute.before Hook）
        /// </summary>
        public async Task<CommandResult> ExecuteAsync(
            CommandContext context,
            Func<CommandContext, CancellationToken, Task<CommandResult>> executor,
            CancellationToken cancellationToken = default)
        {
            if (executor == null)
            {
                throw new ArgumentNullException(nameof(executor));
            }

            _logger.LogInformation("准备执行命令: {CommandName}, SessionId: {SessionId}", 
                context.CommandName, context.SessionId);

            // 触发 command.execute.before Hook
            var output = new Dictionary<string, object>
            {
                ["arguments"] = context.Arguments,
                ["proceed"] = true
            };

            var hookResult = await _hookManager.TriggerAsync(
                HookPoints.CommandExecuteBefore,
                new Dictionary<string, object>
                {
                    ["command"] = context.CommandName,
                    ["sessionId"] = context.SessionId,
                    ["arguments"] = context.Arguments,
                    ["messageId"] = context.MessageId ?? string.Empty
                },
                output,
                cancellationToken);

            // 检查是否被 Hook 中断
            if (!hookResult.Continue || (output.TryGetValue("proceed", out var proceed) && !(bool)proceed))
            {
                _logger.LogWarning("命令执行被 Hook 中断: {CommandName}", context.CommandName);
                return new CommandResult
                {
                    Success = false,
                    Error = "命令执行被 Hook 中断"
                };
            }

            // 应用 Hook 修改后的参数
            if (output.TryGetValue("arguments", out var modifiedArgs) && modifiedArgs is string args)
            {
                context.Arguments = args;
            }

            try
            {
                // 执行实际命令
                var result = await executor(context, cancellationToken);
                
                _logger.LogInformation("命令执行完成: {CommandName}, Success: {Success}", 
                    context.CommandName, result.Success);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "命令执行失败: {CommandName}", context.CommandName);
                
                return new CommandResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }
}