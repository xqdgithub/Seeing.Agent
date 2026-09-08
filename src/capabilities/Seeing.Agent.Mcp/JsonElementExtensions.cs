using System.Text.Json;

namespace Seeing.Agent.Mcp;

/// <summary>
/// JsonElement 扩展（MCP 包内自用，避免依赖主库 Helpers）。
/// </summary>
internal static class JsonElementExtensions
{
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
