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
/// 内置工具目录：逐个登记真实工具类型，供元数据契约测试遍历。
/// <para>
/// 只读取 <see cref="ITool.Description"/> 与 <see cref="ITool.ParametersSchema"/>，不执行任何工具逻辑。
/// 构造参数中接口一律给 Moq 实例（部分基类会做 null 校验），具体类型给 null（元数据读取路径不会解引用）。
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
        Auto<BashTool>(),
        Auto<TaskTool>(),
        Auto<TaskStatusTool>(),
        Auto<TodoWriteTool>(),
        Auto<SessionListTool>(),
        Auto<SessionReadTool>(),
        Auto<SessionSearchTool>(),
        Auto<SessionTrimTool>(),
        Auto<SessionHandoffTool>(),
        Auto<SessionInboxSendTool>(),
        Auto<SessionTakeoverTool>(),
        Auto<SessionReleaseTool>(),
        Auto<MemorySearchTool>(),
        Auto<MemoryReadTool>(),
        Auto<MemoryWriteTool>(),
        Auto<EditorQueryTool>(),
        Auto<EditorListCommandsTool>(),
        Auto<EditorCommandTool>(),
        Auto<CronCreateTool>(),
        Auto<CronListTool>(),
        Auto<CronDisableTool>(),
        Auto<CronResumeTool>(),
        Auto<CronRunTool>(),
        Auto<CronDeleteTool>(),
        Auto<WebSearchTool>(),
        Auto<WebFetchTool>(),
        Auto<CodeSearchTool>(),
        Auto<SkillTool>(),
        Auto<ToolSearchTool>(),
        Auto<GitStatusTool>(),
        Auto<GitDiffTool>(),
        Auto<GitLogTool>(),
        Auto<GitCommitTool>(),
        Auto<SendMessageTool>(),
        Auto<ListConversationsTool>(),
        Auto<ListChannelUsersTool>(),
        Auto<AcpTool>(),
        Auto<AcpStatusTool>(),
        new SystemOneTool(SystemOneToolKind.Ask, Mock.Of<Microsoft.Extensions.Logging.ILogger<SystemOneTool>>()),
        new SystemOneTool(SystemOneToolKind.Noul, Mock.Of<Microsoft.Extensions.Logging.ILogger<SystemOneTool>>()),
        new SystemOneTool(SystemOneToolKind.Choice, Mock.Of<Microsoft.Extensions.Logging.ILogger<SystemOneTool>>()),
        new SystemOneTool(SystemOneToolKind.Score, Mock.Of<Microsoft.Extensions.Logging.ILogger<SystemOneTool>>()),
        Auto<CurrentTimeTool>(),
        Auto<QuestionTool>(),
    ];

    /// <summary>用最少参数的构造函数创建实例。</summary>
    public static ITool Auto<T>() where T : ITool => (ITool)Create(typeof(T));

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
