using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Tools.FileSystem;

namespace Seeing.Agent.Tools.Metadata.Tests;

/// <summary>
/// 内置工具目录：逐个登记真实工具类型，供元数据契约测试遍历。
/// <para>
/// 只读取 <see cref="ITool.Description"/> 与 <see cref="ITool.ParametersSchema"/>，不执行任何工具逻辑。
/// 构造参数统一传 null：内置工具构造函数只做赋值，不校验 null，也不会在元数据读取路径上被解引用。
/// </para>
/// </summary>
internal static class BuiltInToolCatalog
{
    public static IReadOnlyList<ITool> All() =>
    [
        Auto<ReadTool>(),
        Auto<WriteTool>(),
        Auto<EditTool>(),
        Auto<DeleteTool>(),
        Auto<GrepTool>(),
        Auto<GlobTool>(),
        Auto<OpenFileTool>(),
        Auto<AddWorkspacePathTool>(),
    ];

    /// <summary>用最少参数的构造函数创建实例。</summary>
    public static ITool Auto<T>() where T : ITool => (ITool)Create(typeof(T));

    private static object Create(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        return ctor.Invoke(args);
    }
}
