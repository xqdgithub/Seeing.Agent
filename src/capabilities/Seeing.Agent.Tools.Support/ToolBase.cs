using System.Text.Json;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Tools;

namespace Seeing.Agent.Tools.Support;

/// <summary>
/// Tool 基类 - 提供常用 Tool 实现的便捷方法
/// <para>
/// 工具零权限代码：资源级检查集中到 <c>ToolManager</c>（经 <c>IToolPermissionPolicy</c> 映射到
/// 执行级授权器），工具本身不得发起权限审批。
/// </para>
/// </summary>
public abstract class ToolBase : ITool
{
    protected readonly ILogger _logger;

    private IReadOnlyDictionary<string, string>? _capabilities;

    /// <summary>
    /// 创建 Tool 基类实例
    /// </summary>
    protected ToolBase(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>工具 ID</summary>
    public abstract string Id { get; }

    /// <summary>工具描述</summary>
    public abstract string Description { get; }

    /// <summary>工具标签（用于分类和过滤）</summary>
    public virtual IReadOnlyList<string> Tags => Array.Empty<string>();

    /// <summary>工具分类</summary>
    public virtual ToolCategory Category => ToolCategory.General;

    /// <summary>
    /// 工具能力声明（默认从类级 [ToolCapability] 属性读取，惰性缓存）。
    /// 子类可整体覆盖，或直接以 [ToolCapability] 属性声明。
    /// </summary>
    public virtual IReadOnlyDictionary<string, string>? Capabilities =>
        _capabilities ??= ToolCapabilityAttribute.ReadFromType(GetType());

    /// <summary>参数 Schema (JSON Schema)</summary>
    public virtual JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });

    /// <summary>执行工具</summary>
    public abstract Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context);

    /// <summary>
    /// 创建成功结果
    /// </summary>
    protected ToolResult Success(string output, Dictionary<string, object>? metadata = null)
    {
        return new ToolResult
        {
            Success = true,
            Output = output,
            Metadata = metadata ?? new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// 创建带标题的成功结果
    /// </summary>
    protected ToolResult Success(string title, string output, Dictionary<string, object>? metadata = null)
    {
        return new ToolResult
        {
            Success = true,
            Title = title,
            Output = output,
            Metadata = metadata ?? new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// 创建失败结果
    /// </summary>
    protected ToolResult Failure(Exception error)
    {
        _logger.LogError(error, "Tool [{Id}] 执行失败", Id);

        return new ToolResult
        {
            Success = false,
            Error = error.Message
        };
    }

    /// <summary>
    /// 失败（附带自定义消息）
    /// </summary>
    protected ToolResult Failure(Exception error, string message)
    {
        _logger.LogError(error, "Tool [{Id}] 执行失败: {Message}", Id, message);
        return new ToolResult
        {
            Success = false,
            Error = message,
            Metadata = new Dictionary<string, object> { ["exception"] = error.ToString() }
        };
    }

    /// <summary>
    /// 创建失败结果
    /// </summary>
    protected ToolResult Failure(string message)
    {
        _logger.LogWarning("Tool [{Id}] 执行失败: {Message}", Id, message);

        return new ToolResult
        {
            Success = false,
            Error = message
        };
    }

    /// <summary>
    /// 解析 JSON 参数
    /// </summary>
    protected T? ParseArgument<T>(JsonElement arguments, string propertyName, T? defaultValue = default)
    {
        if (arguments.TryGetProperty(propertyName, out var property))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(property.GetRawText());
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "解析参数失败: {PropertyName}", propertyName);
            }
        }

        return defaultValue;
    }

    /// <summary>
    /// 获取字符串参数
    /// </summary>
    protected string? GetStringArgument(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    /// <summary>
    /// 获取整数参数
    /// </summary>
    protected int? GetIntArgument(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number)
        {
            return property.GetInt32();
        }

        return null;
    }

    /// <summary>
    /// 获取布尔参数
    /// </summary>
    protected bool? GetBoolArgument(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property) &&
            (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False))
        {
            return property.GetBoolean();
        }

        return null;
    }

    /// <summary>
    /// 获取字符串数组参数
    /// </summary>
    protected List<string>? GetStringArrayArgument(JsonElement arguments, string propertyName)
    {
        if (arguments.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in property.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is string str)
                {
                    result.Add(str);
                }
            }
            return result.Count > 0 ? result : null;
        }

        return null;
    }
}
