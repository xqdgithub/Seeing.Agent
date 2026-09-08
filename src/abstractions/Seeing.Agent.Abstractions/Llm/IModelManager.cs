using Seeing.Session.Core;

namespace Seeing.Agent.Llm;

/// <summary>
/// 模型域对外唯一门面：目录、默认模型、解析、会话读写。
/// <para>类型位于 Abstractions 程序集；实现由 Core 提供。命名空间保持 <c>Seeing.Agent.Llm</c> 以兼容既有引用。</para>
/// </summary>
public interface IModelManager : IModelConfigManager
{
    string? ResolveNativeModel(string? requestModelRef, string? sessionModelRef, string agentName);
    string? ResolveAcpModel(string? requestModelRef, string? sessionModelRef);
    string GetSessionModelRef(SessionData session);
    bool ApplyModelToSession(SessionData session, string? modelRef);
    bool SeedSessionModel(SessionData session, string agentName);
}
