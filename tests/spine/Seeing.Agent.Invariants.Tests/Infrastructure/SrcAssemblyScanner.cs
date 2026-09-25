using System.Reflection;
using Microsoft.Extensions.Hosting;
using Seeing.Agent.Abstractions.Modules;

namespace Seeing.Agent.Invariants.Tests.Infrastructure;

/// <summary>
/// src 程序集扫描器 — 从仓库构建产物加载 <c>Seeing*.dll</c> 供架构守门测试反射使用。
/// <para>
/// 规避测试项目逐个 ProjectReference 的维护成本：只要 src 下程序集已随解决方案构建，
/// 即可发现其全部 <see cref="ISeeingModule"/> 实现；<c>plugs/</c> 与测试程序集不纳入范围。
/// </para>
/// </summary>
internal static class SrcAssemblyScanner
{
    /// <summary>加载 src 下全部可加载的 Seeing* 程序集（去重、排除测试程序集）。</summary>
    public static IReadOnlyList<(Assembly Assembly, string Path)> LoadSrcAssemblies()
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

    /// <summary>反射发现 src 内全部 <see cref="ISeeingModule"/> 实现的稳定 Id 集合。</summary>
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

    /// <summary>发现能力包（<c>src/capabilities</c>）内的裸 <see cref="IHostedService"/> 实现。</summary>
    public static IReadOnlyList<string> FindBareHostedServiceTypes()
    {
        var hits = new List<string>();
        foreach (var (assembly, path) in LoadSrcAssemblies())
        {
            if (!IsCapabilityAssemblyPath(path))
                continue;

            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;
                if (!typeof(IHostedService).IsAssignableFrom(type))
                    continue;
                if (typeof(IModuleHostedService).IsAssignableFrom(type))
                    continue;

                hits.Add(type.FullName ?? type.Name);
            }
        }

        return hits;
    }

    private static bool IsCapabilityAssemblyPath(string path) =>
        path.Replace('\\', '/').Contains("/src/capabilities/", StringComparison.OrdinalIgnoreCase);

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
