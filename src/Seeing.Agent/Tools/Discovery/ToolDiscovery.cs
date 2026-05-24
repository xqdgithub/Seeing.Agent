using System.Reflection;
using System.Text.Json;

namespace Seeing.Agent.Tools.Discovery
{
    /// <summary>
    /// 反射发现的工具信息
    /// </summary>
    public class DiscoveredTool
    {
        /// <summary>工具 ID</summary>
        public string Id { get; set; } = "";

        /// <summary>工具描述</summary>
        public string Description { get; set; } = "";

        /// <summary>原始方法信息</summary>
        public MethodInfo MethodInfo { get; set; } = null!;

        /// <summary>声明类型</summary>
        public Type DeclaringType { get; set; } = null!;

        /// <summary>参数 Schema</summary>
        public JsonElement ParametersSchema { get; set; }

        /// <summary>是否静态方法</summary>
        public bool IsStatic { get; set; }
    }

    /// <summary>
    /// 工具发现器 - 扫描类型中的工具方法
    /// </summary>
    public class ToolDiscovery
    {
        /// <summary>
        /// 从类型中发现所有工具方法
        /// </summary>
        public static List<DiscoveredTool> DiscoverTools(Type type)
        {
            var tools = new List<DiscoveredTool>();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

            foreach (var method in methods)
            {
                var toolAttr = method.GetCustomAttribute<Attributes.ToolAttribute>();
                if (toolAttr == null) continue;

                var tool = new DiscoveredTool
                {
                    Id = !string.IsNullOrEmpty(toolAttr.Name) ? toolAttr.Name : method.Name,
                    Description = toolAttr.Description,
                    MethodInfo = method,
                    DeclaringType = type,
                    IsStatic = method.IsStatic,
                    ParametersSchema = BuildParametersSchema(method)
                };

                tools.Add(tool);
            }

            return tools;
        }

        /// <summary>
        /// 从类型中发现所有工具方法
        /// </summary>
        public static List<DiscoveredTool> DiscoverTools<T>()
        {
            return DiscoverTools(typeof(T));
        }

        /// <summary>
        /// 构建参数 JSON Schema
        /// </summary>
        private static JsonElement BuildParametersSchema(MethodInfo method)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var param in method.GetParameters())
            {
                var paramAttr = param.GetCustomAttribute<Attributes.ToolParamAttribute>();
                var requiredAttr = param.GetCustomAttribute<Attributes.RequiredAttribute>();

                var paramSchema = BuildTypeSchema(param.ParameterType, paramAttr?.Description ?? "");

                properties[param.Name!] = paramSchema;

                // 必需参数：没有默认值或标记了 Required
                if (requiredAttr != null || (!param.HasDefaultValue && requiredAttr == null))
                {
                    required.Add(param.Name!);
                }
            }

            var schema = new
            {
                type = "object",
                properties,
                required = required.Count > 0 ? required : null
            };

            return JsonSerializer.SerializeToElement(schema);
        }

        /// <summary>
        /// 构建类型的 JSON Schema
        /// </summary>
        private static object BuildTypeSchema(Type type, string description)
        {
            var typeName = GetJsonTypeName(type);
            var schema = new Dictionary<string, object>
            {
                ["type"] = typeName
            };

            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }

            // 处理枚举
            if (type.IsEnum)
            {
                var enumValues = Enum.GetNames(type);
                schema["enum"] = enumValues;
            }

            // 处理数组/List
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            {
                var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
                schema["items"] = BuildTypeSchema(elementType, "");
            }

            // 处理复杂对象
            if (IsComplexType(type))
            {
                var properties = new Dictionary<string, object>();
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead) continue;
                    var propAttr = prop.GetCustomAttribute<Attributes.ToolParamTypeAttribute>();
                    properties[prop.Name] = BuildTypeSchema(prop.PropertyType, propAttr?.Description ?? "");
                }
                schema["properties"] = properties;
            }

            return schema;
        }

        private static string GetJsonTypeName(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "string";
            if (type == typeof(Guid)) return "string";
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))) return "array";
            if (type.IsEnum) return "string";
            return "object";
        }

        private static bool IsComplexType(Type type)
        {
            if (type.IsPrimitive) return false;
            if (type == typeof(string)) return false;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return false;
            if (type == typeof(Guid)) return false;
            if (type.IsEnum) return false;
            if (type.IsArray) return false;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return false;
            if (type.Namespace?.StartsWith("System") == true) return false;
            return type.IsClass || type.IsValueType;
        }
    }
}