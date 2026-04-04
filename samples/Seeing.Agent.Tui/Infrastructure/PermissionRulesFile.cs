using System.Text.Json;
using Seeing.Agent.Core.Interfaces;

namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// 从工作区 seeing.permissions.json 加载 <see cref="PermissionRule"/>。
/// </summary>
public static class PermissionRulesFile
{
    public static IReadOnlyList<PermissionRule> TryLoad(string workspaceRoot)
    {
        var path = Path.Combine(workspaceRoot, "seeing.permissions.json");
        if (!File.Exists(path))
            return Array.Empty<PermissionRule>();

        try
        {
            var json = File.ReadAllText(path);
            var dtos = JsonSerializer.Deserialize<List<PermissionRuleDto>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dtos == null)
                return Array.Empty<PermissionRule>();

            var list = new List<PermissionRule>();
            foreach (var d in dtos)
            {
                if (string.IsNullOrWhiteSpace(d.Permission))
                    continue;
                if (!Enum.TryParse<PermissionAction>(d.Action, true, out var action))
                    action = PermissionAction.Allow;
                list.Add(new PermissionRule
                {
                    Permission = d.Permission.Trim(),
                    Pattern = string.IsNullOrEmpty(d.Pattern) ? "*" : d.Pattern,
                    Action = action
                });
            }

            return list;
        }
        catch
        {
            return Array.Empty<PermissionRule>();
        }
    }

    private sealed class PermissionRuleDto
    {
        public string Permission { get; set; } = "";
        public string Pattern { get; set; } = "*";
        public string Action { get; set; } = "Allow";
    }
}
