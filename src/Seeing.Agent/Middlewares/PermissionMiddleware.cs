using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Middlewares
{
    /// <summary>
    /// 权限中间件 - 在执行前检查权限
    /// </summary>
    public class PermissionMiddleware : IExecutionMiddleware
    {
        private readonly IPermissionService _permissions;
        private readonly ILogger<PermissionMiddleware> _logger;

        /// <inheritdoc />
        public string Name => "Permission";

        /// <inheritdoc />
        public int Order => 50; // 在日志之后

        /// <summary>
        /// 创建权限中间件
        /// </summary>
        public PermissionMiddleware(
            IPermissionService permissions,
            ILogger<PermissionMiddleware> logger)
        {
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<TResult> InvokeAsync<TContext, TResult>(
            ExecutionDelegate<TContext, TResult> next,
            TContext context)
        {
            // 检查上下文是否实现了权限感知接口
            if (context is IPermissionAware permissionCtx)
            {
                // 创建权限上下文
                var permContext = new PermissionContext();

                var result = await _permissions.EvaluateToolAsync(
                    permissionCtx.ToolId,
                    null,
                    permContext);

                _logger.LogDebug(
                    "[Permission] 工具 {ToolId} 权限决策: {Action}",
                    permissionCtx.ToolId, result.Effect);

                switch (result.Effect)
                {
                    case PermissionEffect.Deny:
                        throw new PermissionDeniedException(
                            permissionCtx.ToolId,
                            result.Reason ?? "权限被拒绝");

                    case PermissionEffect.Ask:
                        if (context is IExecutionContext execCtx)
                        {
                            var request = new PermissionRequest
                            {
                                Permission = "tool",
                                Patterns = new List<string> { permissionCtx.ToolId }
                            };

                            var confirmed = await execCtx.PermissionChannel.RequestConfirmationAsync(request);

                            if (!confirmed)
                            {
                                throw new PermissionDeniedException(
                                    permissionCtx.ToolId,
                                    "用户拒绝授权");
                            }
                        }
                        break;
                }
            }

            return await next(context);
        }
    }

    /// <summary>
    /// 权限感知接口 - 需要权限检查的上下文实现此接口
    /// </summary>
    public interface IPermissionAware
    {
        /// <summary>工具 ID</summary>
        string ToolId { get; }
    }

    /// <summary>
    /// 权限拒绝异常
    /// </summary>
    public class PermissionDeniedException : Exception
    {
        /// <summary>被拒绝的资源</summary>
        public string Resource { get; }

        public PermissionDeniedException(string resource, string? reason = null)
            : base($"权限被拒绝: {resource}. {reason}")
        {
            Resource = resource;
        }
    }
}
