using Seeing.Agent.Llm;
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
        public ProviderType ProviderType { get; set; } = ProviderType.OpenAI;

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
    /// 模型类型
    /// </summary>
    public enum ModelType
    {
        Chat,
        Embedding,
        Rerank
    }

    /// <summary>
    /// 会话信息
    /// </summary>
    public class SessionInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? ParentId { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
    }

    /// <summary>
    /// 工具定义
    /// </summary>
    public class ToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public FunctionDefinition? Function { get; set; }
    }

    /// <summary>
    /// 函数定义
    /// </summary>
    public class FunctionDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public object? Parameters { get; set; }
    }

    /// <summary>
    /// 函数工具 Schema
    /// </summary>
    public record FunctionToolSchema
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public FunctionSchema Function { get; set; } = new();
    }

    /// <summary>
    /// 函数 Schema
    /// </summary>
    public record FunctionSchema
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonIgnore]
        public string RealName { get; set; } = string.Empty;

        [JsonIgnore]
        public string ServerName { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public System.Text.Json.JsonElement? Parameters { get; set; }
    }
}