using System.Reflection;

namespace Seeing.Agent.Abstractions.Commands;

/// <summary>
/// 命令发现端口 — 从类型/程序集扫描带注解的命令方法。
/// 实现留在 Core（含 Markdown 发现）；能力包只依赖本接口。
/// </summary>
public interface ICommandDiscovery
{
    /// <summary>从类型发现命令</summary>
    IEnumerable<ICommand> DiscoverFromType(Type type, object? instance = null);

    /// <summary>从类型发现命令（泛型）</summary>
    IEnumerable<ICommand> DiscoverFromType<T>(T? instance = null) where T : class;

    /// <summary>从程序集发现所有命令</summary>
    IEnumerable<ICommand> DiscoverFromAssembly(Assembly assembly, IServiceProvider? services = null);

    /// <summary>从多个程序集发现命令</summary>
    IEnumerable<ICommand> DiscoverFromAssemblies(IEnumerable<Assembly> assemblies, IServiceProvider? services = null);

    /// <summary>从目录发现 Markdown 命令</summary>
    Task<IEnumerable<ICommand>> DiscoverFromMarkdownAsync(
        string directory,
        CancellationToken ct = default);
}
