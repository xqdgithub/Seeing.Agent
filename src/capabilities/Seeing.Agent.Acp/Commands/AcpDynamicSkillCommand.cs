using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Commands;

namespace Seeing.Agent.Acp.Commands;

/// <summary>
/// ACP 动态 Skill 命令 - 透传给 ACP 后端
/// </summary>
public sealed class AcpDynamicSkillCommand : ICommand
{
    public CommandMetadata Metadata { get; }

    public AcpDynamicSkillCommand(string skillName, string? description = null)
    {
        Metadata = new CommandMetadata
        {
            Name = skillName,
            Description = description ?? $"ACP 透传: {skillName}",
            Usage = $"/{skillName} [args]",
            Category = CommandCategory.Tools,
            Type = CommandType.Skill,
            SupportedRuntimes = new[] { AgentRuntime.AcpPassthrough }
        };
    }

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
    {
        // 透传，不修改历史，继续执行 Agent
        return Task.FromResult(CommandResult.Ok(shouldContinue: true));
    }
}
