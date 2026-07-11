using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Commands
{
    /// <summary>
    /// 命令分发器 - 解析和分发命令到注册的处理器
    /// </summary>
    public class CommandDispatcher
    {
        private readonly ICommandRegistry _registry;
        private readonly ICommandService? _commandService;
        private readonly ILogger<CommandDispatcher>? _logger;

        public CommandDispatcher(
            ICommandRegistry registry,
            ICommandService? commandService = null,
            ILogger<CommandDispatcher>? logger = null)
        {
            _registry = registry;
            _commandService = commandService;
            _logger = logger;
        }

        /// <summary>
        /// 处理命令输入
        /// </summary>
        public async Task<CommandResult> HandleAsync(string input, CommandContext context, CancellationToken ct = default)
        {
            // 解析命令
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return CommandResult.Ok();

            var cmdName = parts[0].TrimStart('/');
            var args = parts.Length > 1 ? parts[1].Trim() : "";

            // 查找命令
            var command = _registry.GetCommand(cmdName);
            if (command == null)
            {
                var suggestion = GetSuggestion(cmdName);
                var errorMsg = $"未知命令: /{cmdName}";
                if (suggestion != null)
                    errorMsg += $" (你可能想要: /{suggestion})";
                errorMsg += " (输入 /help 查看帮助)";
                return CommandResult.Fail(errorMsg);
            }

            // 创建新上下文
            var execContext = new CommandContext
            {
                CommandName = command.Metadata.Name,
                RawInput = input,
                Arguments = args,
                SessionId = context.SessionId,
                MessageId = context.MessageId,
                Services = context.Services,
                CancellationToken = ct,
                WorkspaceRoot = context.WorkspaceRoot,
                TargetAgent = command.Metadata.Agent
            };

            try
            {
                // 如果有 CommandService，通过 Hook 机制执行
                if (_commandService != null)
                {
                    return await _commandService.ExecuteAsync(
                        execContext,
                        (ctx, token) => command.ExecuteAsync(ctx, token),
                        ct);
                }

                // 直接执行
                return await command.ExecuteAsync(execContext, ct);
            }
            catch (OperationCanceledException)
            {
                return CommandResult.Ok("操作已取消");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "命令执行错误: {CommandName}", command.Metadata.Name);
                return CommandResult.Fail($"命令执行错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析命令（判断是否为命令）
        /// </summary>
        public static bool IsCommand(string input) =>
            input.StartsWith('/') && input.Length > 1 && !input.StartsWith("//");

        /// <summary>
        /// 获取命令建议（模糊匹配）
        /// </summary>
        private string? GetSuggestion(string input)
        {
            var commands = _registry.GetAllCommands().ToList();

            // 完全匹配前缀
            var prefixMatch = commands.FirstOrDefault(c =>
                c.Metadata.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase));
            if (prefixMatch != null)
                return prefixMatch.Metadata.Name;

            // 包含匹配
            var containsMatch = commands.FirstOrDefault(c =>
                c.Metadata.Name.Contains(input, StringComparison.OrdinalIgnoreCase));
            if (containsMatch != null)
                return containsMatch.Metadata.Name;

            return null;
        }
    }
}