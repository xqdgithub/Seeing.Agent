namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限服务接口 - 统一的权限评估入口
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// 评估资源权限
    /// </summary>
    /// <param name="resource">资源标识符</param>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限评估结果</returns>
    Task<PermissionResult> EvaluateAsync(ResourceIdentifier resource, PermissionContext context, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 评估工具调用权限
    /// </summary>
    /// <param name="toolName">工具名称</param>
    /// <param name="ns">命名空间（可选）</param>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限评估结果</returns>
    Task<PermissionResult> EvaluateToolAsync(string toolName, string? ns, PermissionContext context, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 评估子代理调用权限
    /// </summary>
    /// <param name="agentName">代理名称</param>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限评估结果</returns>
    Task<PermissionResult> EvaluateAgentAsync(string agentName, PermissionContext context, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 评估文件操作权限
    /// </summary>
    /// <param name="filePath">文件路径</param>
    /// <param name="operation">文件操作类型</param>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限评估结果</returns>
    Task<PermissionResult> EvaluateFileAsync(string filePath, FileOperation operation, PermissionContext context, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 评估 MCP 工具调用权限
    /// </summary>
    /// <param name="mcpServer">MCP 服务器名称</param>
    /// <param name="toolName">工具名称</param>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限评估结果</returns>
    Task<PermissionResult> EvaluateMcpToolAsync(string mcpServer, string toolName, PermissionContext context, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 评估技能调用权限
    /// </summary>
    /// <param name="skillName">技能名称</param>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限评估结果</returns>
    Task<PermissionResult> EvaluateSkillAsync(string skillName, PermissionContext context, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取 Agent 的权限策略
    /// </summary>
    /// <param name="agentName">Agent 名称</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>权限策略</returns>
    Task<AgentPermissionPolicy> GetPolicyAsync(string agentName, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 合并全局策略与 Agent 策略
    /// </summary>
    /// <param name="global">全局策略</param>
    /// <param name="agent">Agent 策略</param>
    /// <returns>合并后的策略</returns>
    AgentPermissionPolicy MergePolicies(AgentPermissionPolicy global, AgentPermissionPolicy agent);
    
    /// <summary>
    /// 使缓存失效
    /// </summary>
    /// <param name="agentName">Agent 名称（可选，null 表示所有）</param>
    /// <param name="resourcePattern">资源模式（可选）</param>
    void InvalidateCache(string? agentName = null, string? resourcePattern = null);
    
    /// <summary>
    /// 记录审计日志
    /// </summary>
    /// <param name="result">权限评估结果</param>
    /// <param name="context">权限上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task LogAuditAsync(PermissionResult result, PermissionContext context, CancellationToken cancellationToken = default);
}
