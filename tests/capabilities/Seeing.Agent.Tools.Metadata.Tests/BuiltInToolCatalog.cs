using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Acp.Tools;
using Seeing.Agent.Hosting.Tools;
using Seeing.Agent.Memory.Integration.Tools;
using Seeing.Agent.Scheduler.Tools;
using Seeing.Agent.Skills;
using Seeing.Agent.Tools.Basic;
using Seeing.Agent.Tools.ChannelMessaging;
using Seeing.Agent.Tools.Editor;
using Seeing.Agent.Tools.FileSystem;
using Seeing.Agent.Tools.Git;
using Seeing.Agent.Tools.Question;
using Seeing.Agent.Tools.Session;
using Seeing.Agent.Tools.Shell;
using Seeing.Agent.Tools.SystemOne;
using Seeing.Agent.Tools.ToolDiscovery;
using Seeing.Agent.Tools.Web;

namespace Seeing.Agent.Tools.Metadata.Tests;

/// <summary>
/// 内置工具目录：以**类型**为唯一登记单位，供元数据契约测试遍历。
/// <para>
/// 只读取 <see cref="ITool.Description"/> 与 <see cref="ITool.ParametersSchema"/>，不执行任何工具逻辑。
/// 构造参数中接口一律给 Moq 实例（部分基类会做 null 校验），具体类型给 null（元数据读取路径不会解引用）。
/// </para>
/// <para>
/// 登记单位是类型而不是 id：<see cref="SystemOneTool"/> 一个类型按 kind 产出 4 个 id，
/// 这样"新增工具类型但忘了登记"可以由反射对比 <see cref="Types"/> 发现。
/// </para>
/// </summary>
internal static class BuiltInToolCatalog
{
    /// <summary>全部内置工具类型（每个能力包一个代表性锚点类型另见 <see cref="Assemblies"/>）。</summary>
    private static readonly Type[] s_types =
    [
        typeof(ReadTool),
        typeof(WriteTool),
        typeof(EditTool),
        typeof(DeleteTool),
        typeof(GrepTool),
        typeof(GlobTool),
        typeof(OpenFileTool),
        typeof(AddWorkspacePathTool),
        typeof(BashTool),
        typeof(TaskTool),
        typeof(TaskStatusTool),
        typeof(TodoWriteTool),
        typeof(SessionListTool),
        typeof(SessionReadTool),
        typeof(SessionSearchTool),
        typeof(SessionTrimTool),
        typeof(SessionHandoffTool),
        typeof(SessionInboxSendTool),
        typeof(SessionTakeoverTool),
        typeof(SessionReleaseTool),
        typeof(MemorySearchTool),
        typeof(MemoryReadTool),
        typeof(MemoryWriteTool),
        typeof(EditorQueryTool),
        typeof(EditorListCommandsTool),
        typeof(EditorCommandTool),
        typeof(CronCreateTool),
        typeof(CronListTool),
        typeof(CronDisableTool),
        typeof(CronResumeTool),
        typeof(CronRunTool),
        typeof(CronDeleteTool),
        typeof(WebSearchTool),
        typeof(WebFetchTool),
        typeof(CodeSearchTool),
        typeof(SkillTool),
        typeof(ToolSearchTool),
        typeof(GitStatusTool),
        typeof(GitDiffTool),
        typeof(GitLogTool),
        typeof(GitCommitTool),
        typeof(SendMessageTool),
        typeof(ListConversationsTool),
        typeof(ListChannelUsersTool),
        typeof(AcpTool),
        typeof(AcpStatusTool),
        typeof(SystemOneTool),
        typeof(CurrentTimeTool),
        typeof(QuestionTool),
    ];

    /// <summary>登记的全部工具类型。</summary>
    public static IReadOnlyList<Type> Types() => s_types;

    /// <summary>
    /// 覆盖全部内置工具所在程序集的锚点类型（每个程序集一个）。
    /// 新增整个能力包时需要在此补一个锚点，否则反射对比覆盖不到该程序集。
    /// </summary>
    public static IReadOnlyList<Type> Assemblies() =>
    [
        typeof(ReadTool),          // Seeing.Agent.Tools.FileSystem
        typeof(BashTool),          // Seeing.Agent.Tools.Shell
        typeof(WebSearchTool),     // Seeing.Agent.Tools.Web
        typeof(CurrentTimeTool),   // Seeing.Agent.Tools.Basic
        typeof(SessionListTool),   // Seeing.Agent.Tools.Session
        typeof(EditorQueryTool),   // Seeing.Agent.Tools.Editor
        typeof(MemoryReadTool),    // Seeing.Agent.Memory
        typeof(CronListTool),      // Seeing.Agent.Scheduler
        typeof(AcpTool),           // Seeing.Agent.Acp
        typeof(SystemOneTool),     // Seeing.Agent.Tools.SystemOne
        typeof(QuestionTool),      // Seeing.Agent.Tools.Question
        typeof(TaskTool),          // Seeing.Agent.Hosting
        typeof(ToolSearchTool),    // Seeing.Agent.Tools.ToolDiscovery
        typeof(GitStatusTool),     // Seeing.Agent.Tools.Git
        typeof(SendMessageTool),   // Seeing.Agent.Tools.ChannelMessaging
        typeof(SkillTool),         // Seeing.Agent.Skills
    ];

    public static IReadOnlyList<ITool> All() => s_types.SelectMany(CreateInstances).ToList();

    private static IEnumerable<ITool> CreateInstances(Type type)
    {
        if (type == typeof(SystemOneTool))
        {
            foreach (var kind in Enum.GetValues<SystemOneToolKind>())
                yield return new SystemOneTool(kind, Mock.Of<ILogger<SystemOneTool>>());
            yield break;
        }

        yield return (ITool)Create(type);
    }

    /// <summary>用最少参数的构造函数创建实例。</summary>
    private static object Create(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray();
        return ctor.Invoke(args);
    }

    private static object? Resolve(Type type)
    {
        if (type.IsValueType)
            return Activator.CreateInstance(type);

        if (type.IsInterface)
        {
            // 经泛型方法调用 Mock.Of<T>()，避免反射 Mock<T>.Object 时的二义性（Mock 与 Mock<T> 同名属性）。
            return typeof(BuiltInToolCatalog)
                .GetMethod(nameof(MockOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type)
                .Invoke(null, null);
        }

        return null;
    }

    private static T MockOf<T>() where T : class => Mock.Of<T>();
}
