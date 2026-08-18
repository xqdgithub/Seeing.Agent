// src/Seeing.Agent/Tools/BuiltIn/DefaultToolPermissionPolicy.cs

using Seeing.Agent.Abstractions.Components;
using System.Text.Json;

using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Components;
namespace Seeing.Agent.Tools.BuiltIn;

/// <summary>
/// Default resource permission policy for built-in tools.
/// Maps tool IDs to their resource parameters via a static table.
/// Tools not in the table need no resource-level check and return null.
/// </summary>
public sealed class DefaultToolPermissionPolicy : IToolPermissionPolicy
{
    internal readonly record struct Mapping(
        string PermissionKind,
        string? ArgKey,
        string? FixedResource = null,
        bool TrailingDirSeparator = false,
        string? PatternsArgKey = null,
        Dictionary<string, object>? StaticMetadata = null
    );

    private static readonly Dictionary<string, Mapping> Mappings = new()
    {
        ["read"]                = new("filesystem.read",               "filePath"),
        ["write"]               = new("filesystem.write",              "filePath"),
        ["edit"]                = new("filesystem.write",              "filePath"),
        ["grep"]                = new("filesystem.read",               "path"),
        ["glob"]                = new("filesystem.read",               "path"),
        ["delete"]              = new("filesystem.delete",             "path"),
        ["bash"]                = new("shell.execute",                 "command"),
        ["webfetch"]            = new("network.fetch",                 "url"),
        ["websearch"]           = new("network.search",                null, FixedResource: "web_search"),
        ["codesearch"]          = new("network.search",                null, FixedResource: "code_search"),
        ["add_workspace_path"]  = new("filesystem.workspace_extend",   "path",
                                   TrailingDirSeparator: true,
                                   PatternsArgKey: "path",
                                   StaticMetadata: new() { ["reason"] = "Agent 请求扩展工作区路径" }),
    };

    public PermissionResourceCheck? Evaluate(string toolId, JsonElement args)
    {
        if (!Mappings.TryGetValue(toolId, out var m))
            return null;

        var resource = m.FixedResource
            ?? TryGetStringArg(args, m.ArgKey)
            ?? string.Empty;

        if (m.TrailingDirSeparator && resource.Length > 0 && !resource.EndsWith('/'))
            resource += '/';

        List<string>? patterns = null;
        if (m.PatternsArgKey is not null)
        {
            var pat = TryGetStringArg(args, m.PatternsArgKey);
            if (pat is not null)
                patterns = new List<string> { pat };
        }

        // Build metadata: start with static metadata, then copy all args
        var metadata = m.StaticMetadata != null
            ? new Dictionary<string, object>(m.StaticMetadata)
            : new Dictionary<string, object>();

        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                metadata[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString()
                };
            }
        }

        return new PermissionResourceCheck(
            m.PermissionKind,
            resource,
            Patterns: patterns,
            Metadata: metadata
        );
    }

    private static string? TryGetStringArg(JsonElement args, string? key)
    {
        if (key is null) return null;
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (args.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }
}
