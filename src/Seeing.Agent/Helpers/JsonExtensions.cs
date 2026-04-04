using System.Collections.Generic;
using System.Text.Json;

namespace Seeing.Agent.Helpers
{
    /// <summary>
    /// JsonElement 扩展方法
    /// </summary>
    public static class JsonExtensions
    {
        /// <summary>
        /// 将 JsonElement 转换为 Dictionary&lt;string, object?&gt;
        /// </summary>
        public static Dictionary<string, object?> ToDictionary(this JsonElement element)
        {
            var result = new Dictionary<string, object?>();
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => prop.Value.GetRawText()
                    };
                }
            }
            return result;
        }
    }
}