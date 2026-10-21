using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Session.Core;

namespace Seeing.Agent.Tests;

/// <summary>
/// 执行链测试用空 schema 解析存根：不注入工具，返回空集。
/// 用于不校验 schema 的 ExecutionRunner 单测（组6 后 resolver 为必需注入）。
/// </summary>
internal sealed class StubSessionToolSchemaResolver : ISessionToolSchemaResolver
{
    public Task<SessionToolSchemaResolution> ResolveAsync(
        string sessionId, AgentDefinition agent, CancellationToken cancellationToken = default)
        => Task.FromResult(SessionToolSchemaResolution.Empty);

    public Task<SessionToolSchemaResolution> ResolveAsync(
        SessionData session, AgentDefinition agent, CancellationToken cancellationToken = default)
        => Task.FromResult(SessionToolSchemaResolution.Empty);
}
