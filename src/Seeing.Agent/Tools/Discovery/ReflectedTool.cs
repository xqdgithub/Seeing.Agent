using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Helpers;
using Seeing.Agent.Tools.Attributes;
using System.Reflection;
using System.Text.Json;

namespace Seeing.Agent.Tools.Discovery
{
    /// <summary>
    /// 反射工具包装器 - 将标记了 [Tool] 注解的方法包装为 ITool
    /// </summary>
    public class ReflectedTool : ITool
    {
        private readonly DiscoveredTool _discovered;
        private readonly ILogger<ReflectedTool>? _logger;
        private readonly IServiceProvider? _serviceProvider;

        public string Id => _discovered.Id;
        public string Description => _discovered.Description;
        public JsonElement ParametersSchema => _discovered.ParametersSchema;

        public ReflectedTool(
            DiscoveredTool discovered,
            ILogger<ReflectedTool>? logger = null,
            IServiceProvider? serviceProvider = null)
        {
            _discovered = discovered;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            try
            {
                var args = arguments.ToDictionary();

                var parameters = _discovered.MethodInfo.GetParameters();
                var invokeArgs = new object?[parameters.Length];

                // 验证并转换参数
                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    var paramName = param.Name ?? $"arg{i}";

                    if (args.TryGetValue(paramName, out var jsonValue))
                    {
                        invokeArgs[i] = ConvertParameter(jsonValue, param.ParameterType, paramName);
                    }
                    else if (param.HasDefaultValue)
                    {
                        invokeArgs[i] = param.DefaultValue;
                    }
                    else if (IsRequiredParameter(param))
                    {
                        // 必需参数缺失，返回错误
                        var errorMsg = $"必需参数 '{paramName}' 缺失。请提供该参数后重试。";
                        _logger?.LogWarning("工具 {ToolId} 执行失败：{Error}", _discovered.Id, errorMsg);
                        return new ToolResult
                        {
                            Success = false,
                            Error = errorMsg
                        };
                    }
                    else
                    {
                        // 非必需参数，使用默认值
                        invokeArgs[i] = param.ParameterType.IsValueType
                            ? Activator.CreateInstance(param.ParameterType)
                            : null;
                        _logger?.LogDebug("工具 {ToolId} 参数 {Param} 使用默认值", _discovered.Id, paramName);
                    }
                }

                object? result;
                if (_discovered.IsStatic)
                {
                    result = _discovered.MethodInfo.Invoke(null, invokeArgs);
                }
                else
                {
                    object? instance = null;

                    // 优先从 DI 容器获取实例
                    if (_serviceProvider != null)
                    {
                        instance = _serviceProvider.GetService(_discovered.DeclaringType);
                        if (instance != null)
                        {
                            _logger?.LogDebug("工具 {ToolId} 从 DI 容器获取实例", _discovered.Id);
                        }
                    }

                    // DI 中没有则尝试创建实例
                    if (instance == null)
                    {
                        try
                        {
                            instance = Activator.CreateInstance(_discovered.DeclaringType);
                            _logger?.LogDebug("工具 {ToolId} 通过 Activator 创建实例", _discovered.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "工具 {ToolId} 无法创建实例，请确保类型已注册到 DI 或有公共无参构造函数", _discovered.Id);
                            return new ToolResult
                            {
                                Success = false,
                                Error = $"无法创建工具实例：{_discovered.DeclaringType.Name}。请确保类型已注册到 DI 容器或有公共无参构造函数。"
                            };
                        }
                    }

                    result = _discovered.MethodInfo.Invoke(instance, invokeArgs);
                }

                // 处理 Task 返回值
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    if (task.GetType().IsGenericType)
                    {
                        result = task.GetType().GetProperty("Result")?.GetValue(task);
                    }
                    else
                    {
                        result = null;
                    }
                }

                return new ToolResult
                {
                    Success = true,
                    Output = result?.ToString() ?? "完成"
                };
            }
            catch (TargetInvocationException tie)
            {
                // 解包内部异常
                var innerEx = tie.InnerException ?? tie;
                _logger?.LogError(innerEx, "工具 {ToolId} 执行失败", _discovered.Id);
                return new ToolResult
                {
                    Success = false,
                    Error = $"工具执行异常：{innerEx.Message}"
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "工具 {ToolId} 执行失败", _discovered.Id);
                return new ToolResult
                {
                    Success = false,
                    Error = $"工具执行异常：{ex.Message}"
                };
            }
        }

        /// <summary>
        /// 检查参数是否为必需
        /// </summary>
        private static bool IsRequiredParameter(ParameterInfo param)
        {
            // 有默认值则非必需
            if (param.HasDefaultValue)
                return false;

            // 检查 [Required] 特性
            if (param.GetCustomAttribute<RequiredAttribute>() != null)
                return true;

            // 检查 nullable 类型（Nullable<T> 或 nullable 引用类型）
            var type = param.ParameterType;
            if (Nullable.GetUnderlyingType(type) != null)
                return false;

            // 值类型无默认值则必需
            if (type.IsValueType)
                return true;

            // 引用类型默认不强制必需（除非标记 [Required]）
            return false;
        }

        /// <summary>
        /// 将 object? 值转换为目标类型
        /// </summary>
        private object? ConvertParameter(object? value, Type targetType, string paramName)
        {
            if (value == null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                {
                    _logger?.LogWarning("参数 {Param} 值为 null 但目标类型 {Type} 是非空值类型", paramName, targetType.Name);
                }
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            // 如果值已经是目标类型，直接返回
            if (targetType.IsAssignableFrom(value.GetType()))
                return value;

            // 尝试直接转换
            try
            {
                // 字符串类型
                if (targetType == typeof(string))
                    return value?.ToString();

                // 数值类型
                if (targetType == typeof(int) && value is double d)
                    return (int)d;
                if (targetType == typeof(int))
                    return Convert.ToInt32(value);

                if (targetType == typeof(long) && value is double d2)
                    return (long)d2;
                if (targetType == typeof(long))
                    return Convert.ToInt64(value);

                if (targetType == typeof(double))
                    return Convert.ToDouble(value);

                if (targetType == typeof(decimal))
                    return Convert.ToDecimal(value);

                // 布尔类型
                if (targetType == typeof(bool))
                    return Convert.ToBoolean(value);

                // 日期时间
                if (targetType == typeof(DateTime))
                {
                    if (value is string s)
                        return DateTime.Parse(s);
                    return Convert.ToDateTime(value);
                }

                // Guid
                if (targetType == typeof(Guid) && value is string guidStr)
                    return Guid.Parse(guidStr);

                // 枚举
                if (targetType.IsEnum)
                {
                    if (value is string enumStr)
                        return Enum.Parse(targetType, enumStr, true);
                    return Enum.ToObject(targetType, value);
                }

                // 尝试 JSON 反序列化（针对复杂对象）
                if (value is string jsonStr)
                {
                    try
                    {
                        return JsonSerializer.Deserialize(jsonStr, targetType);
                    }
                    catch (JsonException je)
                    {
                        _logger?.LogWarning(je, "参数 {Param} JSON 反序列化失败，尝试其他转换", paramName);
                    }
                }

                return Convert.ChangeType(value, targetType);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "参数 {Param} 转换失败，目标类型: {Type}", paramName, targetType.Name);
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }
        }
    }

    /// <summary>
    /// 工具包装器工厂
    /// </summary>
    public static class ToolWrapperFactory
    {
        /// <summary>
        /// 从类型创建工具包装器列表
        /// </summary>
        public static List<ITool> CreateTools(
            Type type, 
            ILoggerFactory? loggerFactory = null,
            IServiceProvider? serviceProvider = null)
        {
            var discovered = ToolDiscovery.DiscoverTools(type);
            var tools = new List<ITool>();

            foreach (var d in discovered)
            {
                var logger = loggerFactory?.CreateLogger<ReflectedTool>();
                tools.Add(new ReflectedTool(d, logger, serviceProvider));
            }

            return tools;
        }

        /// <summary>
        /// 从类型创建工具包装器列表
        /// </summary>
        public static List<ITool> CreateTools<T>(
            ILoggerFactory? loggerFactory = null,
            IServiceProvider? serviceProvider = null)
        {
            return CreateTools(typeof(T), loggerFactory, serviceProvider);
        }
    }
}