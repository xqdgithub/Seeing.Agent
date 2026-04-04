using System.Text.Json;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 工具上下文
    /// </summary>
    public class ToolContext
    {
        public string SessionId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string? CallId { get; set; }
        public IAgent? Agent { get; set; }
        public CancellationToken CancellationToken { get; set; }
        
        /// <summary>设置元数据</summary>
        public Action<string, Dictionary<string, object>?>? SetMetadata { get; set; }
        
        /// <summary>请求权限确认</summary>
        public Func<PermissionRequest, Task>? AskPermission { get; set; }
    }

    /// <summary>
    /// 权限请求
    /// </summary>
    public class PermissionRequest
    {
        public string Permission { get; set; } = string.Empty;
        public List<string> Patterns { get; set; } = new();
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// 工具结果
    /// </summary>
    public class ToolResult
    {
        public bool Success { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = new();
        public List<FileAttachment>? Attachments { get; set; }
    }

    /// <summary>
    /// 文件附件
    /// </summary>
    public class FileAttachment
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
    }

    /// <summary>
    /// 工具接口 - LLM 可调用的工具
    /// </summary>
    public interface ITool
    {
        /// <summary>工具 ID</summary>
        string Id { get; }
        
        /// <summary>工具描述</summary>
        string Description { get; }
        
        /// <summary>工具标签（用于分类和过滤）</summary>
        IReadOnlyList<string> Tags => Array.Empty<string>();
        
        /// <summary>工具分类</summary>
        ToolCategory Category => ToolCategory.General;
        
        /// <summary>参数 Schema (JSON Schema)</summary>
        JsonElement ParametersSchema { get; }
        
        /// <summary>执行工具</summary>
        Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context);
    }

    /// <summary>
    /// 工具分类
    /// </summary>
    public enum ToolCategory
    {
        /// <summary>通用工具</summary>
        General,
        
        /// <summary>文件系统</summary>
        FileSystem,
        
        /// <summary>网络请求</summary>
        Network,
        
        /// <summary>数据库操作</summary>
        Database,
        
        /// <summary>计算处理</summary>
        Computation,
        
        /// <summary>LLM 交互</summary>
        LlmInteraction,
        
        /// <summary>外部服务</summary>
        ExternalService
    }
}