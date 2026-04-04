namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// 规则：<c>~/.seeing/rules</c>，再项目 <c>rules/</c>、<c>.seeing/rules</c>、<c>.agent/rules</c>；同名相对路径后写入覆盖。
/// </summary>
public static class RulesMarkdownLoader
{
    public static (string Text, IReadOnlyList<string> Sources) Load(string workspaceRoot)
    {
        var byKey = new Dictionary<string, (string SourceLabel, string Body)>(StringComparer.OrdinalIgnoreCase);

        IngestRulesDirectory(SeeingLayout.UserRulesDirectory, "~/.seeing/rules", byKey);
        IngestRulesDirectory(SeeingLayout.ProjectRulesDirectory(workspaceRoot), "rules", byKey);
        IngestRulesDirectory(SeeingLayout.ProjectSeeingRulesDirectory(workspaceRoot), ".seeing/rules", byKey);
        IngestRulesDirectory(SeeingLayout.ProjectAgentRulesDirectory(workspaceRoot), ".agent/rules", byKey);

        if (byKey.Count == 0)
            return ("", Array.Empty<string>());

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Project rules and agent guidance");
        sb.AppendLine();

        var ordered = byKey.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var kv in ordered)
        {
            var (label, body) = kv.Value;
            sb.AppendLine($"### {label}/{kv.Key}");
            sb.AppendLine(body.Trim());
            sb.AppendLine();
        }

        var sources = ordered.Select(k => $"{k.Value.SourceLabel}/{k.Key}").ToList();
        return (sb.ToString().TrimEnd(), sources);
    }

    private static void IngestRulesDirectory(
        string rulesRoot,
        string sourceLabel,
        Dictionary<string, (string SourceLabel, string Body)> byKey)
    {
        if (!Directory.Exists(rulesRoot))
            return;

        foreach (var file in Directory.GetFiles(rulesRoot, "*.md", SearchOption.AllDirectories))
        {
            try
            {
                var rel = Path.GetRelativePath(rulesRoot, file);
                var key = rel.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                if (string.IsNullOrEmpty(key))
                    continue;

                byKey[key] = (sourceLabel, File.ReadAllText(file));
            }
            catch
            {
                // skip
            }
        }
    }
}
