using Seeing.Agent.Configuration;

namespace Seeing.Agent.MCP.Core;

/// <summary>
/// MCP 配置管理器接口 - 提供服务器配置的管理、验证和持久化能力
/// </summary>
public interface IMcpConfigManager
{
    #region 服务器管理

    /// <summary>
    /// 添加新的服务器配置
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="config">服务器配置</param>
    /// <param name="level">配置保存级别（可选，默认自动选择）</param>
    /// <param name="persist">是否在添加后自动持久化到文件（默认 true）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    Task<McpOperationResult> AddServerAsync(
        string name,
        McpServerConfig config,
        ConfigLevel? level = null,
        bool persist = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除服务器配置
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="persist">是否在移除后自动持久化到文件（默认 true）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    Task<McpOperationResult> RemoveServerAsync(
        string name,
        bool persist = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新服务器配置
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="config">新的服务器配置</param>
    /// <param name="persist">是否在更新后自动持久化到文件（默认 true）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    Task<McpOperationResult> UpdateConfigAsync(
        string name,
        McpServerConfig config,
        bool persist = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启用服务器（设置 Disabled = false）
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="persist">是否在启用后自动持久化到文件（默认 true）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    Task<McpOperationResult> EnableServerAsync(
        string name,
        bool persist = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 禁用服务器（设置 Disabled = true）
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="persist">是否在禁用后自动持久化到文件（默认 true）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>操作结果</returns>
    Task<McpOperationResult> DisableServerAsync(
        string name,
        bool persist = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 从 JSON 导入服务器配置
    /// </summary>
    /// <param name="json">JSON 配置字符串</param>
    /// <param name="level">配置保存级别</param>
    /// <param name="overwrite">是否覆盖已存在的同名服务器</param>
    /// <param name="persist">是否在导入后自动持久化到文件（默认 true）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>导入的服务器数量</returns>
    Task<int> ImportFromJsonAsync(
        string json,
        ConfigLevel level,
        bool overwrite = false,
        bool persist = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 重新加载所有配置（从文件）
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>重新加载的服务器数量</returns>
    Task<int> ReloadAllAsync(CancellationToken cancellationToken = default);

    #endregion

    #region 配置验证与查询

    /// <summary>
    /// 验证服务器配置
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="config">服务器配置</param>
    /// <returns>验证结果（是否有效，错误信息）</returns>
    (bool Valid, string? Error) ValidateConfig(string name, McpServerConfig config);

    /// <summary>
    /// 获取指定服务器的配置
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <returns>服务器配置，不存在则返回 null</returns>
    McpServerConfig? GetConfig(string name);

    /// <summary>
    /// 获取所有服务器配置
    /// </summary>
    /// <returns>服务器名称到配置的只读字典</returns>
    IReadOnlyDictionary<string, McpServerConfig> GetAllConfigs();

    /// <summary>
    /// 获取指定服务器配置所在的级别
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <returns>配置级别，不存在则返回 null</returns>
    ConfigLevel? GetConfigLevel(string name);

    #endregion

    #region 持久化

    /// <summary>
    /// 保存指定级别的配置到文件
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步任务</returns>
    Task SaveConfigAsync(ConfigLevel level, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定级别的配置文件路径
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <returns>配置文件完整路径</returns>
    string GetConfigFilePath(ConfigLevel level);

    /// <summary>
    /// 获取指定级别的所有配置作为 JSON 字符串
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <param name="indented">是否格式化输出</param>
    /// <returns>JSON 字符串</returns>
    string GetConfigsAsJson(ConfigLevel level, bool indented = true);

    /// <summary>
    /// 获取单个服务器配置作为 JSON 字符串
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="indented">是否格式化输出</param>
    /// <returns>JSON 字符串，不存在则返回 null</returns>
    string? GetServerConfigAsJson(string name, bool indented = true);

    #endregion
}