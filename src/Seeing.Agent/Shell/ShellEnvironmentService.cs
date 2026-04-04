using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Hooks;

namespace Seeing.Agent.Shell
{
    /// <summary>
    /// Shell 环境服务接口 - 用于在执行 shell 命令前获取环境变量
    /// </summary>
    public interface IShellEnvironmentService
    {
        /// <summary>
        /// 获取 Shell 环境变量（触发 shell.env Hook）
        /// </summary>
        /// <param name="cwd">工作目录</param>
        /// <param name="sessionId">会话 ID（可选）</param>
        /// <param name="callId">调用 ID（可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>环境变量字典</returns>
        Task<Dictionary<string, string>> GetEnvironmentAsync(
            string cwd,
            string? sessionId = null,
            string? callId = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Shell 环境服务实现 - 通过 Hook 机制允许外部注入环境变量
    /// </summary>
    public class ShellEnvironmentService : IShellEnvironmentService
    {
        private readonly ILogger<ShellEnvironmentService> _logger;
        private readonly IHookManager _hookManager;

        public ShellEnvironmentService(
            ILogger<ShellEnvironmentService> logger,
            IHookManager hookManager)
        {
            _logger = logger;
            _hookManager = hookManager;
        }

        /// <summary>
        /// 获取 Shell 环境变量（触发 shell.env Hook）
        /// </summary>
        public async Task<Dictionary<string, string>> GetEnvironmentAsync(
            string cwd,
            string? sessionId = null,
            string? callId = null,
            CancellationToken cancellationToken = default)
        {
            // 初始输出为空字典，Hook 可以修改
            var output = new Dictionary<string, object>
            {
                ["env"] = new Dictionary<string, string>()
            };

            // 触发 shell.env Hook
            await _hookManager.TriggerAsync(
                HookPoints.ShellEnv,
                new Dictionary<string, object>
                {
                    ["cwd"] = cwd,
                    ["sessionId"] = sessionId ?? string.Empty,
                    ["callId"] = callId ?? string.Empty
                },
                output,
                cancellationToken);

            // 提取环境变量
            if (output.TryGetValue("env", out var envObj) && envObj is Dictionary<string, string> env)
            {
                _logger.LogDebug("Shell 环境变量已通过 Hook 注入: {Count} 个", env.Count);
                return env;
            }

            // 如果返回的是其他类型，尝试转换
            if (envObj is IDictionary<string, object> envDict)
            {
                var result = new Dictionary<string, string>();
                foreach (var (key, value) in envDict)
                {
                    if (value != null)
                    {
                        result[key] = value.ToString() ?? string.Empty;
                    }
                }
                return result;
            }

            return new Dictionary<string, string>();
        }
    }
}