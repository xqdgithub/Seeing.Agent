using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;

namespace Seeing.Agent.Configuration;

/// <summary>
/// Bridges <see cref="IOptionsMonitor{TOptions}"/> for nested <see cref="ShellOptions"/> from <see cref="SeeingAgentOptions"/>.
/// </summary>
internal sealed class ShellOptionsMonitor : IOptionsMonitor<ShellOptions>
{
    private readonly IOptionsMonitor<SeeingAgentOptions> _agentOptions;

    public ShellOptionsMonitor(IOptionsMonitor<SeeingAgentOptions> agentOptions)
    {
        _agentOptions = agentOptions;
    }

    public ShellOptions CurrentValue => _agentOptions.CurrentValue.Shell;

    public ShellOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<ShellOptions, string?> listener) =>
        _agentOptions.OnChange((options, name) => listener(options.Shell, name));
}
