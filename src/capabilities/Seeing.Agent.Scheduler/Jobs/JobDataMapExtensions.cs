using Quartz;

namespace Seeing.Agent.Scheduler.Jobs;

/// <summary>
/// JobDataMap 类型安全的扩展方法
/// 在 UseProperties=true 模式下，所有值必须是字符串，此工具类提供自动类型转换
/// </summary>
public static class JobDataMapExtensions
{
    // ===== 存储方法（对象 → 字符串）=====

    /// <summary>设置字符串值</summary>
    public static void SetStringValue(this JobDataMap map, string key, string? value)
    {
        map[key] = value ?? string.Empty;
    }

    /// <summary>设置整数值（转换为字符串存储）</summary>
    public static void SetIntValue(this JobDataMap map, string key, int value)
    {
        map[key] = value.ToString();
    }

    /// <summary>设置布尔值（转换为字符串存储）</summary>
    public static void SetBoolValue(this JobDataMap map, string key, bool value)
    {
        map[key] = value.ToString().ToLowerInvariant();
    }

    /// <summary>设置可空整数值</summary>
    public static void SetNullableIntValue(this JobDataMap map, string key, int? value)
    {
        map[key] = value?.ToString() ?? string.Empty;
    }

    /// <summary>设置可空布尔值</summary>
    public static void SetNullableBoolValue(this JobDataMap map, string key, bool? value)
    {
        map[key] = value?.ToString().ToLowerInvariant() ?? string.Empty;
    }

    /// <summary>设置对象为 JSON 字符串</summary>
    public static void SetJsonValue<T>(this JobDataMap map, string key, T? value)
    {
        map[key] = value != null 
            ? System.Text.Json.JsonSerializer.Serialize(value) 
            : string.Empty;
    }

    // ===== 读取方法（字符串 → 对象）=====

    /// <summary>安全获取字符串值（key 不存在时返回 null，不抛异常）</summary>
    public static string? GetStringValue(this JobDataMap map, string key)
    {
        if (!map.ContainsKey(key))
            return null;
        
        var value = map.GetString(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>获取整数值（从字符串解析）</summary>
    public static int GetIntValue(this JobDataMap map, string key, int defaultValue = 0)
    {
        var strValue = map.GetStringValue(key);
        if (string.IsNullOrEmpty(strValue))
            return defaultValue;

        return int.TryParse(strValue, out var result) ? result : defaultValue;
    }

    /// <summary>获取布尔值（从字符串解析）</summary>
    public static bool GetBoolValue(this JobDataMap map, string key, bool defaultValue = false)
    {
        var strValue = map.GetStringValue(key);
        if (string.IsNullOrEmpty(strValue))
            return defaultValue;

        return bool.TryParse(strValue, out var result) ? result : defaultValue;
    }

    /// <summary>获取可空整数值</summary>
    public static int? GetNullableIntValue(this JobDataMap map, string key)
    {
        var strValue = map.GetStringValue(key);
        if (string.IsNullOrEmpty(strValue))
            return null;

        return int.TryParse(strValue, out var result) ? result : null;
    }

    /// <summary>获取可空布尔值</summary>
    public static bool? GetNullableBoolValue(this JobDataMap map, string key)
    {
        var strValue = map.GetStringValue(key);
        if (string.IsNullOrEmpty(strValue))
            return null;

        return bool.TryParse(strValue, out var result) ? result : null;
    }

    /// <summary>从 JSON 字符串获取对象</summary>
    public static T? GetJsonValue<T>(this JobDataMap map, string key) where T : class
    {
        var strValue = map.GetStringValue(key);
        if (string.IsNullOrEmpty(strValue))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(strValue);
        }
        catch
        {
            return null;
        }
    }

    // ===== 批量操作 =====

    /// <summary>创建 JobDataMap 的便捷方法（支持类型安全）</summary>
    public static JobDataMap Create(params (string key, object? value)[] items)
    {
        var map = new JobDataMap();
        foreach (var (key, value) in items)
        {
            switch (value)
            {
                case null:
                    map[key] = string.Empty;
                    break;
                case string str:
                    map[key] = str;
                    break;
                case int intValue:
                    map.SetIntValue(key, intValue);
                    break;
                case bool boolValue:
                    map.SetBoolValue(key, boolValue);
                    break;
                default:
                    // 其他类型序列化为 JSON
                    map.SetJsonValue(key, value);
                    break;
            }
        }
        return map;
    }
}
