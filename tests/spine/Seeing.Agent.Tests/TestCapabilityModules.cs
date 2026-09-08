using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Agents.BuiltIn;
using Seeing.Agent.Core.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Extensions;
using Seeing.Agent.Llm.Anthropic;
using Seeing.Agent.Llm.OpenAI;
using Seeing.Agent.Mcp;
using Seeing.Agent.Skills;
using Seeing.IO.Local;
using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Tests;

/// <summary>
/// 测试宿主最小能力模块登记（io.local / skills / mcp / llm / agents.builtin）。
/// </summary>
public static class TestCapabilityModules
{
    public static IServiceCollection AddMinimalCapabilityModules(
        this IServiceCollection services,
        IConfigSectionRegistry registry)
    {
        services.AddSeeingModule<LocalExecutionWorldModule>(registry);
        services.AddSeeingModule<SkillsModule>(registry);
        services.AddSeeingModule<McpModule>(registry);
        services.AddSeeingModule<OpenAiLlmModule>(registry);
        services.AddSeeingModule<AnthropicLlmModule>(registry);
        services.AddSeeingModule<AgentsBuiltInModule>(registry);
        return services;
    }
}
