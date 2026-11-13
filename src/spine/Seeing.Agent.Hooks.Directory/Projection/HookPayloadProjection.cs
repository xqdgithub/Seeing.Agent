using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seeing.Agent.Hooks.Directory.Projection;

public static class HookPayloadProjection
{
    // JSON null 在公开 API 里是 C# null，GetValueKind 不能在空引用上调用。
    // 内部 JsonValueOfElement 持有 ValueKind.Null 的元素，读出来仍是 Null。
    private static readonly Lazy<ConstructorInfo> s_nullNodeCtor = new(ResolveNullNodeCtor);

    public static JsonObject Project(
        IReadOnlyDictionary<string, object?>? bag,
        Action<string>? onDropped = null)
    {
        var result = new JsonObject();
        if (bag is null)
            return result;

        foreach (var (key, value) in bag)
        {
            if (TryProject(value, onDropped, out var node))
                result[key] = node;
            else
                onDropped?.Invoke(key);
        }

        return result;
    }

    public static bool TryWrite(
        IDictionary<string, object?> mutable,
        JsonElement returned,
        out string? error)
    {
        if (returned.ValueKind != JsonValueKind.Object)
        {
            error = "写回值必须是 JSON 对象";
            return false;
        }

        var pending = new List<KeyValuePair<string, object?>>();
        foreach (var property in returned.EnumerateObject())
        {
            if (!mutable.ContainsKey(property.Name))
                continue;

            if (!TryConvert(mutable[property.Name], property.Value, out var converted, out var convertError))
            {
                error = $"键 '{property.Name}' 无法写回：{convertError}";
                return false;
            }

            pending.Add(new KeyValuePair<string, object?>(property.Name, converted));
        }

        foreach (var (key, value) in pending)
            mutable[key] = value;

        error = null;
        return true;
    }

    private static bool TryProject(object? value, Action<string>? onDropped, out JsonNode? node)
    {
        switch (value)
        {
            case null:
                node = NullNode();
                return true;
            case string text:
                node = JsonValue.Create(text);
                return true;
            case bool flag:
                node = JsonValue.Create(flag);
                return true;
            case JsonElement element when element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null:
                node = NullNode();
                return true;
            case JsonElement element:
                node = JsonNode.Parse(Clone(element).GetRawText()) ?? NullNode();
                return true;
            case Exception exception:
                node = new JsonObject
                {
                    ["type"] = exception.GetType().FullName,
                    ["message"] = exception.Message,
                };
                return true;
            case byte number:
                node = JsonValue.Create(number);
                return true;
            case short number:
                node = JsonValue.Create(number);
                return true;
            case int number:
                node = JsonValue.Create(number);
                return true;
            case long number:
                node = JsonValue.Create(number);
                return true;
            case float number:
                node = JsonValue.Create(number);
                return true;
            case double number:
                node = JsonValue.Create(number);
                return true;
            case decimal number:
                node = JsonValue.Create(number);
                return true;
        }

        if (value is IDictionary<string, string> strings)
        {
            node = ProjectPairs(Pairs(strings), onDropped);
            return true;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            node = ProjectPairs(readOnly, onDropped);
            return true;
        }

        if (value is IDictionary<string, object?> objects)
        {
            node = ProjectPairs(objects, onDropped);
            return true;
        }

        if (value is IEnumerable enumerable)
        {
            node = ProjectArray(enumerable, onDropped);
            return true;
        }

        node = null;
        return false;
    }

    private static JsonObject ProjectPairs(
        IEnumerable<KeyValuePair<string, object?>> pairs,
        Action<string>? onDropped)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in pairs)
        {
            if (TryProject(value, onDropped, out var child))
                obj[key] = child;
            else
                onDropped?.Invoke(key);
        }

        return obj;
    }

    private static IEnumerable<KeyValuePair<string, object?>> Pairs(IDictionary<string, string> source)
    {
        foreach (var pair in source)
            yield return new KeyValuePair<string, object?>(pair.Key, pair.Value);
    }

    private static JsonArray ProjectArray(IEnumerable source, Action<string>? onDropped)
    {
        var array = new JsonArray();
        foreach (var item in source)
        {
            if (TryProject(item, onDropped, out var child))
                array.Add(child);
        }

        return array;
    }

    private static bool TryConvert(object? current, JsonElement incoming, out object? converted, out string? error)
    {
        if (current is JsonElement)
        {
            converted = Clone(incoming);
            error = null;
            return true;
        }

        if (current is IDictionary<string, string>)
            return TryConvertStringDictionary(incoming, out converted, out error);

        if (current is null)
            return TryConvertNull(incoming, out converted, out error);

        switch (current)
        {
            case string:
                if (incoming.ValueKind != JsonValueKind.String)
                    return Fail("需要字符串", out converted, out error);
                converted = incoming.GetString();
                error = null;
                return true;
            case bool:
                if (incoming.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Fail("需要布尔", out converted, out error);
                converted = incoming.GetBoolean();
                error = null;
                return true;
            case byte or short or int or long:
                if (incoming.ValueKind != JsonValueKind.Number)
                    return Fail("需要数字", out converted, out error);
                try
                {
                    converted = ConvertInteger(current, incoming.GetDouble());
                    error = null;
                    return true;
                }
                catch (OverflowException)
                {
                    return Fail("数字溢出", out converted, out error);
                }
            case float or double or decimal:
                if (incoming.ValueKind != JsonValueKind.Number)
                    return Fail("需要数字", out converted, out error);
                try
                {
                    converted = ConvertFloat(current, incoming.GetDouble());
                    error = null;
                    return true;
                }
                catch (OverflowException)
                {
                    return Fail("数字溢出", out converted, out error);
                }
            default:
                return Fail("不支持的值类型", out converted, out error);
        }
    }

    private static bool TryConvertStringDictionary(JsonElement incoming, out object? converted, out string? error)
    {
        if (incoming.ValueKind != JsonValueKind.Object)
            return Fail("字符串字典必须是对象", out converted, out error);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in incoming.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                return Fail("字符串字典的值必须是字符串", out converted, out error);

            var text = property.Value.GetString();
            if (text is null || !map.TryAdd(property.Name, text))
                return Fail("字符串字典含无效或重复键", out converted, out error);
        }

        converted = map;
        error = null;
        return true;
    }

    private static bool TryConvertNull(JsonElement incoming, out object? converted, out string? error)
    {
        switch (incoming.ValueKind)
        {
            case JsonValueKind.String:
                converted = incoming.GetString();
                error = null;
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                converted = incoming.GetBoolean();
                error = null;
                return true;
            case JsonValueKind.Null:
                converted = null;
                error = null;
                return true;
            case JsonValueKind.Number:
                converted = incoming.TryGetInt32(out var integer) ? integer : incoming.GetDouble();
                error = null;
                return true;
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                converted = Clone(incoming);
                error = null;
                return true;
            default:
                return Fail("null 槽无法接受该 JSON", out converted, out error);
        }
    }

    private static object ConvertInteger(object current, double number) => current switch
    {
        byte => (object)Convert.ToByte(number),
        short => (object)Convert.ToInt16(number),
        int => (object)Convert.ToInt32(number),
        long => (object)Convert.ToInt64(number),
        _ => throw new InvalidOperationException(),
    };

    private static object ConvertFloat(object current, double number) => current switch
    {
        float => (object)Convert.ToSingle(number),
        double => (object)Convert.ToDouble(number),
        decimal => (object)Convert.ToDecimal(number),
        _ => throw new InvalidOperationException(),
    };

    private static JsonElement Clone(JsonElement element)
    {
        using var document = JsonDocument.Parse(element.GetRawText());
        return document.RootElement.Clone();
    }

    private static JsonNode NullNode()
    {
        using var document = JsonDocument.Parse("null");
        var element = document.RootElement.Clone();
        return (JsonNode)s_nullNodeCtor.Value.Invoke([element, null]);
    }

    private static ConstructorInfo ResolveNullNodeCtor()
    {
        var type = typeof(JsonNode).Assembly.GetType("System.Text.Json.Nodes.JsonValueOfElement")
            ?? throw new InvalidOperationException("System.Text.Json 缺少 JsonValueOfElement。");
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   .SingleOrDefault(ctor => ctor.GetParameters() is [{ ParameterType: var first }, _]
                       && first == typeof(JsonElement))
               ?? throw new InvalidOperationException("JsonValueOfElement 构造函数不存在。");
    }

    private static bool Fail(string reason, out object? converted, out string? error)
    {
        converted = null;
        error = reason;
        return false;
    }
}
