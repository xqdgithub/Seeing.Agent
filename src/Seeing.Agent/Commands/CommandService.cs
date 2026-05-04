using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Commands
{
    /// <summary>
    /// 命令执行服务接口 - 用于执行自定义命令并触发相关 Hook
    /// <para>
    /// 此接口用于 Hook 拦截机制，与 ICommand 接口配合使用。
    /// ICommand 定义命令契约，CommandService 提供 Hook 拦截能力。
    /// </para>
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
        private readonly Core.Hooks.IHookManager _hookManager;

        public CommandService(
            ILogger<CommandService> logger,
            Core.Hooks.IHookManager hookManager)
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

            var hookResult = await _hookManager.TriggerBlockingAsync(
                HookRegistry.CommandExecuteBefore,
                context.SessionId ?? string.Empty,
                new Dictionary<string, object?>
                {
                    ["command"] = context.CommandName,
                    ["arguments"] = context.Arguments,
                    ["messageId"] = context.MessageId ?? string.Empty
                },
                output,
                cancellationToken);

            // 检查是否被 Hook 中断
            if (!hookResult.Continue || (output.TryGetValue("proceed", out var proceed) && !(bool)proceed))
            {
                _logger.LogWarning("命令执行被 Hook 中断: {CommandName}", context.CommandName);
                return CommandResult.Fail("命令执行被 Hook 中断");
            }

            // 应用 Hook 修改后的参数
            if (output.TryGetValue("arguments", out var modifiedArgs) && modifiedArgs is string args)
            {
                // 创建新的上下文（因为 Arguments 是 init-only）
                context = new CommandContext
                {
                    CommandName = context.CommandName,
                    RawInput = context.RawInput,
                    Arguments = args,
                    SessionId = context.SessionId,
                    MessageId = context.MessageId,
                    Services = context.Services,
                    CancellationToken = context.CancellationToken,
                    WorkspaceRoot = context.WorkspaceRoot
                };
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
                return CommandResult.Fail(ex.Message);
            }
        }
    }
}