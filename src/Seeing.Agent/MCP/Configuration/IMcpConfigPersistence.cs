using Seeing.Agent.MCP.Core;
using System.Text.Json;

namespace Seeing.Agent.MCP.Configuration;

/// <summary>
/// MCP 配置持久化接口 - 负责配置文件的读取、写入和序列化
/// </summary>
public interface IMcpConfigPersistence
{
    /// <summary>
    /// 加载指定级别的配置
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>服务器名称到配置的只读字典</returns>
    Task<IReadOnlyDictionary<string, McpServerConfig>> LoadAsync(
        McpConfigLevel level,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存指定级别的配置
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <param name="configs">服务器配置字典</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>异步任务</returns>
    Task SaveAsync(
        McpConfigLevel level,
        IReadOnlyDictionary<string, McpServerConfig> configs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 解析单个服务器配置
    /// </summary>
    /// <param name="name">服务器名称</param>
    /// <param name="element">JSON 元素</param>
    /// <returns>解析后的服务器配置，解析失败返回 null</returns>
    McpServerConfig? ParseServerConfig(string name, JsonElement element);

    /// <summary>
    /// 序列化单个服务器配置
    /// </summary>
    /// <param name="config">服务器配置</param>
    /// <returns>序列化后的 JSON 字符串</returns>
    string SerializeServerConfig(McpServerConfig config);

    /// <summary>
    /// 获取指定级别配置文件的路径
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <returns>配置文件的完整路径</returns>
    string GetConfigPath(McpConfigLevel level);

    /// <summary>
    /// 检查指定级别的配置文件是否存在
    /// </summary>
    /// <param name="level">配置级别</param>
    /// <returns>文件是否存在</returns>
    bool ConfigExists(McpConfigLevel level);
}
