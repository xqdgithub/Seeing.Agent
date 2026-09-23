using Seeing.Agent.Core.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Abstractions.Llm;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// Seeing Agent 配置 - 从 seeing.json 加载
    /// </summary>
    public class SeeingAgentConfig
    {
        /// <summary>配置名称</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>配置描述</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>API 基础地址</summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>API 密钥</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>模型标识</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>提供商类型</summary>
        public string ProviderType { get; set; } = ProviderTypes.OpenAi;

        /// <summary>是否支持工具调用</summary>
        public bool Tool { get; set; } = true;

        /// <summary>最大 Token 数</summary>
        public int MaxTokens { get; set; } = 4096;

        /// <summary>请求超时时间（秒）</summary>
        public int Timeout { get; set; } = 30;

        /// <summary>最大重试次数</summary>
        public int MaxRetries { get; set; } = 3;
    }

    /// <summary>
    /// 会话信息
    /// </summary>
    public class SessionInfo
    {
        /// <summary>会话 ID</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>会话标题</summary>
        public string Title { get; set; } = string.Empty;
        /// <summary>创建时间</summary>
        public DateTime CreatedAt { get; set; }
        /// <summary>最后更新时间</summary>
        public DateTime UpdatedAt { get; set; }
        /// <summary>父会话 ID（子代理会话时非空）</summary>
        public string? ParentId { get; set; }
        /// <summary>会话消息列表</summary>
        public List<ChatMessage> Messages { get; set; } = new();
    }

    /// <summary>
    /// 工具定义
    /// </summary>
    public class ToolDefinition
    {
        /// <summary>定义类型（固定为 function）</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        /// <summary>函数签名定义</summary>
        [JsonPropertyName("function")]
        public FunctionDefinition? Function { get; set; }
    }

    /// <summary>
    /// 函数定义
    /// </summary>
    public class FunctionDefinition
    {
        /// <summary>函数（工具）名称</summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>函数功能描述</summary>
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>参数 JSON Schema</summary>
        [JsonPropertyName("parameters")]
        public object? Parameters { get; set; }
    }

}