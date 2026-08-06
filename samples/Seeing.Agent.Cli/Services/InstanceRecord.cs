namespace Seeing.Agent.Cli.Services;

public sealed class InstanceRecord
{
    public string Id { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string WorkspaceRoot { get; set; } = string.Empty;
    public int Port { get; set; }
    public DateTime StartedAt { get; set; }
}