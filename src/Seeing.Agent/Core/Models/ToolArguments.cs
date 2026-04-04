using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Seeing.Agent.Helpers;

namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// 工具参数 - 包装 JsonElement 提供便捷访问
    /// </summary>
    public class ToolArguments
    {
        private readonly JsonElement _raw;
        private readonly Dictionary<string, object?> _parsed;

        /// <summary>
        /// 原始 JSON 元素
        /// </summary>
        public JsonElement Raw => _raw;

        /// <summary>
        /// 参数数量
        /// </summary>
        public int Count => _parsed.Count;

        /// <summary>
        /// 从 JsonElement 创建工具参数
        /// </summary>
        public ToolArguments(JsonElement raw)
        {
            _raw = raw;
            _parsed = raw.ToDictionary();
        }

        /// <summary>
        /// 从字典创建工具参数
        /// </summary>
        public ToolArguments(Dictionary<string, object?> args)
        {
            _parsed = args ?? new Dictionary<string, object?>();
            _raw = JsonSerializer.SerializeToElement(_parsed);
        }

        /// <summary>
        /// 从对象创建工具参数
        /// </summary>
        public static ToolArguments FromObject(object obj)
        {
            var json = JsonSerializer.SerializeToElement(obj);
            return new ToolArguments(json);
        }

        /// <summary>
        /// 获取参数值
        /// </summary>
        public T? Get<T>(string name)
        {
            if (string.IsNullOrEmpty(name) || !_parsed.TryGetValue(name, out var value))
                return default;

            if (value is T typedValue)
                return typedValue;

            // 尝试转换
            try
            {
                if (value is JsonElement je)
                {
                    return JsonSerializer.Deserialize<T>(je.GetRawText());
                }
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// 获取必需参数值（不存在则抛异常）
        /// </summary>
        public T GetRequired<T>(string name)
        {
            if (!Has(name))
                throw new ToolParameterMissingException(name, typeof(T));

            var value = Get<T>(name);
            if (value == null || (typeof(T).IsValueType && value.Equals(default(T))))
                throw new ToolParameterMissingException(name, typeof(T));

            return value!;
        }

        /// <summary>
        /// 检查参数是否存在
        /// </summary>
        public bool Has(string name)
        {
            return !string.IsNullOrEmpty(name) && _parsed.ContainsKey(name);
        }

        /// <summary>
        /// 尝试获取参数值
        /// </summary>
        public bool TryGet<T>(string name, out T? value)
        {
            value = Get<T>(name);
            return Has(name);
        }

        /// <summary>
        /// 获取所有参数名
        /// </summary>
        public IEnumerable<string> GetNames()
        {
            return _parsed.Keys;
        }

        /// <summary>
        /// 计算参数哈希（用于缓存）
        /// </summary>
        public string ComputeHash()
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_raw.GetRawText()));
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// 转换为字典
        /// </summary>
        public Dictionary<string, object?> ToDictionary()
        {
            return new Dictionary<string, object?>(_parsed);
        }

        public override string ToString()
        {
            return _raw.GetRawText();
        }
    }

    /// <summary>
    /// 工具参数缺失异常
    /// </summary>
    public class ToolParameterMissingException : Exception
    {
        /// <summary>参数名</summary>
        public string ParameterName { get; }
        
        /// <summary>期望类型</summary>
        public Type ExpectedType { get; }

        public ToolParameterMissingException(string parameterName, Type expectedType)
            : base($"缺少必需参数 '{parameterName}' (类型: {expectedType.Name})")
        {
            ParameterName = parameterName;
            ExpectedType = expectedType;
        }
    }
}