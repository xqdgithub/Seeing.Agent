namespace Seeing.Agent.Core.Hooks;

public record DataField<T>(
    string Name,
    bool Required = false,
    bool Mutable = false,
    bool InResult = false,
    string? Description = null,
    T? DefaultValue = default)
{
    public T? GetFrom(IReadOnlyDictionary<string, object?> dict)
    {
        if (dict.TryGetValue(Name, out var value))
        {
            if (value is T typedValue) return typedValue;
            if (value == null) return DefaultValue;

            try { return (T)Convert.ChangeType(value, typeof(T)); }
            catch
            {
                System.Diagnostics.Debug.WriteLine($"DataField '{Name}' 类型转换失败: {value.GetType().Name} -> {typeof(T).Name}");
                return DefaultValue;
            }
        }

        if (Required)
            throw new KeyNotFoundException($"Required field '{Name}' not found");

        return DefaultValue;
    }

    public void SetTo(IDictionary<string, object?> dict, T? value)
    {
        dict[Name] = value;
    }
}