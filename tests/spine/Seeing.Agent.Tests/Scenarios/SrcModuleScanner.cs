using Seeing.Agent.Abstractions.Modules;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

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
    /// <summary>
    /// 按程序集名从 <c>src/</c> 构建产物读取其声明的全部类型全名（纯元数据读取，不加载依赖）。
    /// </summary>
    /// <param name="assemblyName">程序集简名，如 <c>Seeing.Agent.Memory</c>。</param>
    /// <returns>类型全名集合；找不到产物或读取失败返回 null。</returns>
    public static IReadOnlyList<string>? TryReadSrcAssemblyTypeNames(string assemblyName)
    {
        var path = FindSrcAssemblyPath(assemblyName);
        if (path is null)
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            var reader = peReader.GetMetadataReader();
            return reader.TypeDefinitions
                .Select(handle =>
                {
                    var definition = reader.GetTypeDefinition(handle);
                    var ns = reader.GetString(definition.Namespace);
                    var name = reader.GetString(definition.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                })
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindSrcAssemblyPath(string assemblyName)
    {
        var root = FindRepoRoot();
        var srcRoot = Path.Combine(root, "src");
        if (!Directory.Exists(srcRoot))
            return null;

        var config = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

        var fileName = assemblyName + ".dll";
        return Directory
            .EnumerateFiles(srcRoot, fileName, SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                path.Contains(
                    $"{Path.DirectorySeparatorChar}{config}{Path.DirectorySeparatorChar}net10.0",
                    StringComparison.OrdinalIgnoreCase));
    }

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
            var projectDir = Directory.GetParent(binDir)?.FullName;
            if (projectDir is null || !Directory.EnumerateFiles(projectDir, "*.csproj").Any())
                continue;

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
