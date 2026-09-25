using System.Reflection;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Tests.Scenarios;

/// <summary>
/// src 能力模块反射扫描器 — 从解决方案构建产物发现全部 <see cref="ISeeingModule"/> 实现的 Id。
/// <para>
/// 用于 full 场景守门：新增模块若未登记进 <c>BuiltInCapabilitySets.Full</c> 即失败。
/// 范围限定 <c>src/</c> 构建产物（plugs 与测试程序集不纳入）。
/// </para>
/// </summary>
internal static class SrcModuleScanner
{
    public static IReadOnlyList<string> DiscoverImplementedModuleIds()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (assembly, _) in LoadSrcAssemblies())
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || !typeof(ISeeingModule).IsAssignableFrom(type))
                    continue;

                try
                {
                    if (Activator.CreateInstance(type) is ISeeingModule module
                        && !string.IsNullOrWhiteSpace(module.Id))
                    {
                        ids.Add(module.Id);
                    }
                }
                catch
                {
                    // 非无参构造的模块由 DI 工厂登记，反射发现不纳入
                }
            }
        }

        return ids.ToList();
    }

    private static IReadOnlyList<(Assembly Assembly, string Path)> LoadSrcAssemblies()
    {
        var root = FindRepoRoot();
        var config = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var result = new List<(Assembly, string)>();
        var srcRoot = Path.Combine(root, "src");
        if (!Directory.Exists(srcRoot))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binDir in Directory.EnumerateDirectories(srcRoot, "bin", SearchOption.AllDirectories))
        {
            var netDir = Path.Combine(binDir, config, "net10.0");
            if (!Directory.Exists(netDir))
                continue;

            foreach (var dll in Directory.EnumerateFiles(netDir, "Seeing*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(name))
                    continue;

                try
                {
                    result.Add((Assembly.LoadFrom(dll), dll));
                }
                catch
                {
                    // 依赖缺失等场景跳过该程序集，不阻断其余扫描
                }
            }
        }

        return result;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Seeing.Agent.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}
