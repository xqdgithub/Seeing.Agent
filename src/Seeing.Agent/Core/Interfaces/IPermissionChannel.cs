using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 扩展的权限请求 - 包含更多上下文信息
    /// </summary>
    public class ExtendedPermissionRequest : PermissionRequest
    {
        /// <summary>请求类型（tool/subagent/write/read）</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>目标名称（工具名/代理名/文件路径）</summary>
        public string Target { get; set; } = string.Empty;

        /// <summary>参数或内容预览</summary>
        public object? Arguments { get; set; }

        /// <summary>会话 ID</summary>
        public string SessionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 权限请求通道接口 - 处理需要用户确认的权限请求
    /// <para>
    /// 支持多入口模式：
    /// - TUI: 终端交互式确认
    /// - API: HTTP 响应等待确认
    /// - CLI: 自动/配置模式
    /// </para>
    /// </summary>
    public interface IPermissionChannel
    {
        /// <summary>请求权限确认</summary>
        /// <param name="request">权限请求</param>
        /// <returns>用户是否批准</returns>
        Task<bool> RequestConfirmationAsync(PermissionRequest request);

        /// <summary>
        /// 请求工具执行权限
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="arguments">工具参数</param>
        /// <param name="context">执行上下文</param>
        /// <returns>权限决策结果</returns>
        Task<PermissionDecision> RequestToolPermissionAsync(
            string toolName,
            object? arguments,
            AgentContext context);

        /// <summary>
        /// 请求子代理调用权限
        /// </summary>
        /// <param name="agentName">代理名称</param>
        /// <param name="prompt">调用提示</param>
        /// <param name="context">执行上下文</param>
        /// <returns>权限决策结果</returns>
        Task<PermissionDecision> RequestSubAgentPermissionAsync(
            string agentName,
            string prompt,
            AgentContext context);

        /// <summary>
        /// 请求文件写入权限
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="contentPreview">内容预览</param>
        /// <param name="context">执行上下文</param>
        /// <returns>权限决策结果</returns>
        Task<PermissionDecision> RequestWritePermissionAsync(
            string filePath,
            string? contentPreview,
            AgentContext context);
    }

    /// <summary>
    /// 默认权限通道 - 安全默认：拒绝所有操作
    /// <para>
    /// 安全设计：默认拒绝，只有用户明确配置 AutoApproveAll=true 时才允许。
    /// 这防止 Agent 在用户不知情的情况下执行危险操作。
    /// </para>
    /// </summary>
    public class DefaultPermissionChannel : IPermissionChannel
    {
        /// <summary>是否自动批准所有请求（危险！仅在用户明确选择时启用）</summary>
        public bool AutoApproveAll { get; init; } = false;

        /// <summary>单例实例（默认拒绝模式）</summary>
        public static readonly DefaultPermissionChannel Instance = new();

        /// <summary>自动批准所有请求的实例（危险！）</summary>
        public static readonly DefaultPermissionChannel AutoApproveInstance = new() { AutoApproveAll = true };

        /// <inheritdoc/>
        public Task<bool> RequestConfirmationAsync(PermissionRequest request)
        {
            if (AutoApproveAll)
            {
                return Task.FromResult(true);
            }

            // 安全默认：拒绝并提示用户配置权限通道
            throw new PermissionRequiredException(
                "权限请求",
                "未配置权限确认通道。请在配置中设置 Permission:AutoApproveAll=true 以自动批准所有操作（危险），或提供 IPermissionChannel 实现（如 ConsolePermissionChannel）。");
        }

        /// <inheritdoc/>
        public Task<PermissionDecision> RequestToolPermissionAsync(
            string toolName,
            object? arguments,
            AgentContext context)
        {
            if (AutoApproveAll)
            {
                return Task.FromResult(PermissionDecision.Allow());
            }

            throw new PermissionRequiredException(
                $"工具调用: {toolName}",
                "未配置权限确认通道。工具调用需要用户确认。");
        }

        /// <inheritdoc/>
        public Task<PermissionDecision> RequestSubAgentPermissionAsync(
            string agentName,
            string prompt,
            AgentContext context)
        {
            if (AutoApproveAll)
            {
                return Task.FromResult(PermissionDecision.Allow());
            }

            throw new PermissionRequiredException(
                $"子代理调用: {agentName}",
                "未配置权限确认通道。子代理调用需要用户确认。");
        }

        /// <inheritdoc/>
        public Task<PermissionDecision> RequestWritePermissionAsync(
            string filePath,
            string? contentPreview,
            AgentContext context)
        {
            if (AutoApproveAll)
            {
                return Task.FromResult(PermissionDecision.Allow());
            }

            throw new PermissionRequiredException(
                $"文件写入: {filePath}",
                "未配置权限确认通道。文件写入操作需要用户确认。");
        }
    }

    /// <summary>
    /// 权限请求异常 - 表示需要配置权限通道
    /// </summary>
    public class PermissionRequiredException : Exception
    {
        /// <summary>请求的资源</summary>
        public string Resource { get; }

        public PermissionRequiredException(string resource, string message)
            : base($"[{resource}] {message}")
        {
            Resource = resource;
        }
    }

    /// <summary>
    /// IPermissionChannel 扩展方法
    /// </summary>
    public static class PermissionChannelExtensions
    {
        /// <summary>
        /// 扩展方法：从旧接口适配
        /// </summary>
        public static IPermissionChannel WithConfirmationHandler(
            this IPermissionChannel channel,
            Func<PermissionRequest, Task<bool>> handler)
        {
            if (channel is PermissionChannelAdapter adapter)
            {
                adapter.SetConfirmationHandler(handler);
                return adapter;
            }
            return new PermissionChannelAdapter(handler);
        }
    }

    /// <summary>
    /// 权限通道适配器 - 支持旧接口
    /// </summary>
    internal class PermissionChannelAdapter : IPermissionChannel
    {
        private Func<PermissionRequest, Task<bool>>? _handler;

        public PermissionChannelAdapter(Func<PermissionRequest, Task<bool>>? handler = null)
        {
            _handler = handler;
        }

        public void SetConfirmationHandler(Func<PermissionRequest, Task<bool>> handler)
        {
            _handler = handler;
        }

        public async Task<bool> RequestConfirmationAsync(PermissionRequest request)
        {
            if (_handler != null)
            {
                return await _handler(request);
            }
            return true; // 默认允许
        }

        public Task<PermissionDecision> RequestToolPermissionAsync(
            string toolName,
            object? arguments,
            AgentContext context)
        {
            return Task.FromResult(PermissionDecision.Allow());
        }

        public Task<PermissionDecision> RequestSubAgentPermissionAsync(
            string agentName,
            string prompt,
            AgentContext context)
        {
            return Task.FromResult(PermissionDecision.Allow());
        }

        public Task<PermissionDecision> RequestWritePermissionAsync(
            string filePath,
            string? contentPreview,
            AgentContext context)
        {
            return Task.FromResult(PermissionDecision.Allow());
        }
    }
}