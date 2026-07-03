using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace Seeing.Gateway.Configuration;

/// <summary>
/// 从 Options 类型反射生成配置表单 Schema
/// </summary>
public static class OptionsSchemaBuilder
{
    private static readonly HashSet<string> SkippedPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enabled",
        "SectionName"
    };

    public static IReadOnlyList<ConfigFieldSchema> FromType(Type optionsType)
    {
        var fields = new List<ConfigFieldSchema>();
        var sample = Activator.CreateInstance(optionsType);

        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite)
                continue;

            if (SkippedPropertyNames.Contains(property.Name))
                continue;

            if (property.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false })
                continue;

            var fieldType = ResolveFieldType(property);
            if (fieldType is null)
                continue;

            var display = property.GetCustomAttribute<DisplayAttribute>();
            var description = property.GetCustomAttribute<DescriptionAttribute>();
            var required = property.GetCustomAttribute<RequiredAttribute>() != null
                || IsImplicitlyRequired(property);

            fields.Add(new ConfigFieldSchema(
                Name: property.Name,
                Label: display?.Name ?? SplitCamelCase(property.Name),
                Description: display?.Description ?? description?.Description,
                Type: fieldType.Value,
                Required: required,
                DefaultValue: sample is null ? null : property.GetValue(sample),
                EnumValues: fieldType.Value == ConfigFieldType.Enum
                    ? Enum.GetNames(property.PropertyType)
                    : null,
                Section: display?.GroupName));
        }

        return fields;
    }

    private static ConfigFieldType? ResolveFieldType(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var name = property.Name;

        if (type == typeof(bool))
            return ConfigFieldType.Boolean;

        if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return ConfigFieldType.Number;

        if (type.IsEnum)
            return ConfigFieldType.Enum;

        if (type == typeof(List<string>) || type == typeof(string[]))
            return ConfigFieldType.StringList;

        if (type != typeof(string))
            return null;

        if (name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase))
            return ConfigFieldType.Secret;

        if (name.Contains("Url", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Uri", StringComparison.OrdinalIgnoreCase))
            return ConfigFieldType.Url;

        if (name.Contains("Text", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Prompt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Message", StringComparison.OrdinalIgnoreCase))
            return ConfigFieldType.TextArea;

        return ConfigFieldType.String;
    }

    private static bool IsImplicitlyRequired(PropertyInfo property)
    {
        if (property.GetCustomAttribute<RequiredAttribute>() != null)
            return true;

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type != typeof(string))
            return false;

        var name = property.Name;
        return name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("BotId", StringComparison.OrdinalIgnoreCase);
    }

    private static string SplitCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return string.Concat(value.Select((c, i) =>
            i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1])
                ? " " + c
                : c.ToString()));
    }
}
