using System.Reflection;
using Seeing.Agent.Abstractions.Hooks;

namespace Seeing.Agent.Hooks.Directory;

public static class HookPointNames
{
    private static readonly string[] LegacyPoints =
    [
        "chat.params",
        "chat.headers",
        "chat.message",
        "chat.after_complete",
        "chat.on_error",
    ];

    private static readonly Dictionary<string, HookSpec> Known = Build();

    public static bool IsLegacy(string point) =>
        LegacyPoints.Contains(point, StringComparer.OrdinalIgnoreCase);

    public static HookSpec Resolve(string point)
    {
        if (Known.TryGetValue(point, out var spec))
            return spec;
        return new HookSpec(HookPolicy.Blocking, point);
    }

    private static Dictionary<string, HookSpec> Build()
    {
        var map = new Dictionary<string, HookSpec>(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        foreach (var field in typeof(HookRegistry).GetFields(flags))
        {
            if (field.GetValue(null) is HookSpec spec && !string.IsNullOrEmpty(spec.Point))
                map.TryAdd(spec.Point, spec);
        }
        return map;
    }
}
