namespace Seeing.Agent.Abstractions.Modules;

/// <summary>
/// 模块描述符 — 目录中的只读快照
/// </summary>
public sealed record ModuleDescriptor(
    string Id,
    IReadOnlyList<string> ProvidedTools,
    IReadOnlyList<string> ProvidedSeams,
    IReadOnlyList<string> DependsOn);
