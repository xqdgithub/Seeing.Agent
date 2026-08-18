// src/Seeing.Agent/Core/Interfaces/IToolPermissionPolicy.cs

using System.Text.Json;

using Seeing.Agent.Abstractions.Permissions;
namespace Seeing.Agent.Core.Interfaces;

/// <summary>
/// Resource-level permission policy for tool invocations.
/// Evaluated by ToolManager after tool.execute.before hooks,
/// before the tool's ExecuteAsync is called.
/// </summary>
public interface IToolPermissionPolicy
{
    /// <summary>
    /// Returns a resource-level permission check for the given tool invocation,
    /// or null if no resource-level check is required for this tool.
    /// Called after tool.execute.before hooks have run,
    /// so <paramref name="args"/> reflects any hook modifications.
    /// </summary>
    PermissionResourceCheck? Evaluate(string toolId, JsonElement args);
}

/// <summary>
/// Describes a resource permission check to be performed
/// via IPermissionChannel before tool execution.
/// </summary>
public record PermissionResourceCheck(
    string PermissionKind,
    string Resource,
    List<string>? Patterns = null,
    Dictionary<string, object>? Metadata = null
);
