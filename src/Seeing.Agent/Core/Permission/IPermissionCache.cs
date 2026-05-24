using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 权限缓存接口 - 提供权限决策的 TTL 缓存
/// </summary>
public interface IPermissionCache
{
    /// <summary>
    /// 获取缓存的权限决策
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <returns>权限动作</returns>
    PermissionAction Get(PermissionCacheKey key);
    
    /// <summary>
    /// 设置缓存条目
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <param name="action">权限动作</param>
    /// <param name="ttl">生存时间（可选）</param>
    void Set(PermissionCacheKey key, PermissionAction action, TimeSpan? ttl = null);
    
    /// <summary>
    /// 尝试获取缓存的权限决策（不触发评估）
    /// </summary>
    /// <param name="key">缓存键</param>
    /// <param name="action">权限动作</param>
    /// <returns>是否命中缓存</returns>
    bool TryGet(PermissionCacheKey key, out PermissionAction action);
    
    /// <summary>
    /// 使指定键的缓存失效
    /// </summary>
    /// <param name="key">缓存键</param>
    void Invalidate(PermissionCacheKey key);
    
    /// <summary>
    /// 使所有包含指定权限的缓存失效
    /// </summary>
    /// <param name="permission">权限名称</param>
    void InvalidateByPermission(string permission);
    
    /// <summary>
    /// 使所有包含指定 Agent 的缓存失效
    /// </summary>
    /// <param name="agentName">Agent 名称</param>
    void InvalidateByAgent(string agentName);
    
    /// <summary>
    /// 清空所有缓存
    /// </summary>
    void Clear();
    
    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    /// <returns>(总条目数, 过期条目数)</returns>
    (int TotalEntries, int ExpiredEntries) GetStats();
}
