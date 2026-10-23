using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Seeing.Agent.Tools.Metadata.Tests;

/// <summary>
/// 内置工具元数据契约：描述规范、参数描述完整性、目录行可读性、兄弟工具路由。
/// </summary>
public class ToolMetadataTests
{
    public static TheoryData<string> ToolIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in BuiltInToolCatalog.All().Select(t => t.Id))
            data.Add(id);
        return data;
    }

    /// <summary>工具 id → 描述中必须点名的兄弟工具 id（双向路由）。</summary>
    private static readonly Dictionary<string, string[]> RoutingExpectations = new()
    {
        ["read"] = ["open_file"],
        ["write"] = ["edit"],
        ["edit"] = ["read", "write"],
        ["glob"] = ["grep"],
        ["grep"] = ["glob"],
        ["open_file"] = ["read"],
        ["bash"] = ["read", "grep"],
        ["task"] = ["task_status"],
        ["task_status"] = ["task"],
        ["session_list"] = ["session_read"],
        ["session_read"] = ["session_search"],
        ["session_search"] = ["session_read"],
        ["session_trim"] = ["session_handoff"],
        ["session_handoff"] = ["session_trim"],
        ["session_takeover"] = ["session_release"],
        ["session_release"] = ["session_takeover"],
        ["memory_search"] = ["memory_read"],
        ["memory_read"] = ["memory_search"],
        ["editor_query"] = ["open_file"],
        ["editor_list_commands"] = ["editor_command"],
        ["editor_command"] = ["editor_list_commands"],
        ["cron_disable"] = ["cron_resume"],
        ["cron_resume"] = ["cron_disable"],
        ["cron_delete"] = ["cron_disable"],
        ["websearch"] = ["webfetch"],
        ["webfetch"] = ["websearch"],
        ["codesearch"] = ["websearch"],
        ["git_status"] = ["git_diff"],
        ["git_diff"] = ["git_status"],
        ["git_commit"] = ["git_status", "git_diff"],
        ["send_channel_message"] = ["list_channel_conversations", "list_channel_users"],
        ["list_channel_conversations"] = ["send_channel_message"],
        ["list_channel_users"] = ["send_channel_message"],
        ["acp"] = ["task"],
        ["acp_status"] = ["task_status"],
        ["systemone_ask"] = ["systemone_noul", "systemone_choice", "systemone_score"],
        ["systemone_noul"] = ["systemone_ask"],
        ["systemone_choice"] = ["systemone_ask"],
        ["systemone_score"] = ["systemone_ask"],
    };

    private static IReadOnlyList<string> Routing(string id) =>
        RoutingExpectations.TryGetValue(id, out var v) ? v : [];

    [Theory, MemberData(nameof(ToolIds))]
    public void Description_ShouldFollowConventions(string id)
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        var text = tool.Description;
        var firstLine = text.Split('\n')[0].Trim();

        text.Should().NotBeNullOrWhiteSpace();
        Regex.IsMatch(text, "[\u4e00-\u9fff]").Should().BeTrue($"{id} 描述必须包含中文");
        firstLine.Length.Should().BeLessThanOrEqualTo(60, $"{id} 首句过长：{firstLine}");
        text.Length.Should().BeLessThanOrEqualTo(700, $"{id} 描述过长");
        text.Split('\n').Should().NotContain(l => l.TrimStart().StartsWith("## "), $"{id} 不应使用 Markdown 标题");
    }

    [Theory, MemberData(nameof(ToolIds))]
    public void Description_ShouldNameSiblingTools(string id)
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        foreach (var sibling in Routing(id))
            tool.Description.Should().Contain($"`{sibling}`", $"{id} 必须点名 {sibling}");
    }

    [Theory, MemberData(nameof(ToolIds))]
    public void Parameters_ShouldDescribeEveryProperty(string id)
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        AssertDescribed(tool.ParametersSchema, id, string.Empty);
    }

    [Theory, MemberData(nameof(ToolIds))]
    public void CatalogLine_ShouldKeepFirstTwentyChars(string id)
    {
        var options = new ToolExposureOptions();
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        var firstLine = tool.Description.Split('\n')[0].Trim();
        var head = firstLine[..Math.Min(20, firstLine.Length)];

        BuildCatalogLine(tool, options.CatalogLineMaxChars).Should().Contain(head);
    }

    [Fact]
    public void Bash_ShouldOnlyRequireCommand()
    {
        var bash = BuiltInToolCatalog.All().Single(t => t.Id == "bash");
        var required = bash.ParametersSchema.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        required.Should().Equal("command");
    }

    [Fact]
    public void AcpStatus_ShouldNotAdvertiseParametersItDoesNotHave()
    {
        // 实现只有必填 task_id：没有 wait / timeout_ms，也不支持不传时列出全部。
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == "acp_status");

        tool.Description.Should().NotContain("wait");
        tool.Description.Should().NotContain("timeout_ms");
        tool.Description.Should().NotContain("不传则");
    }

    [Fact]
    public void Bash_ShouldNotClaimDangerousCommandsAreRejectedByDefault()
    {
        // ShellOptions.EnableCommandGuard 默认 false，不能给模型一个假的"已被拦截"保证。
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == "bash");

        tool.Description.Should().NotContain("危险命令会被拒绝执行");
    }

    [Fact]
    public void Grep_ShouldOnlyAdvertiseDirectoryPaths()
    {
        // 传文件路径会让 rg 的 WorkingDirectory 失效并退化为空结果，属静默误报。
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == "grep");
        var path = tool.ParametersSchema.GetProperty("properties").GetProperty("path")
            .GetProperty("description").GetString();

        path.Should().Contain("目录").And.NotContain("或文件");
    }

    [Fact]
    public void Read_ShouldNotClaimArbitraryBinariesBecomeAttachments()
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == "read");

        tool.Description.Should().NotContain("二进制、图片、PDF");
    }

    [Fact]
    public void TodoWrite_ShouldDescribeItemShape()
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == "todowrite");
        var items = tool.ParametersSchema.GetProperty("properties").GetProperty("todos").GetProperty("items");
        var props = items.GetProperty("properties");

        props.TryGetProperty("content", out _).Should().BeTrue();
        props.GetProperty("status").GetProperty("enum").GetArrayLength().Should().Be(5);
        props.GetProperty("priority").GetProperty("enum").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void Catalog_ShouldCoverEveryBuiltInToolTypeAndId()
    {
        // 1) 工厂登记的类型必须等于能力包程序集里实际存在的 ITool 实现（防"新增工具漏登记"）。
        var actualTypes = BuiltInToolCatalog.Assemblies()
            .Select(a => a.Assembly)
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(ITool).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .ToHashSet();

        actualTypes.Should().BeEquivalentTo(BuiltInToolCatalog.Types());

        // 2) id 集合仍按产品事实锁定（SystemOneTool 一个类型 → 4 个 id）。
        var ids = BuiltInToolCatalog.All().Select(t => t.Id).ToArray();

        ids.Should().HaveCount(52);
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().BeEquivalentTo(new[]
        {
            "read", "write", "edit", "delete", "grep", "glob", "open_file", "add_workspace_path", "bash",
            "task", "task_status", "todowrite",
            "session_list", "session_read", "session_search", "session_trim", "session_handoff",
            "session_inbox_send", "session_takeover", "session_release",
            "memory_search", "memory_read", "memory_write",
            "editor_query", "editor_list_commands", "editor_command",
            "cron_create", "cron_list", "cron_disable", "cron_resume", "cron_run", "cron_delete",
            "websearch", "webfetch", "codesearch", "skill", "tool_search",
            "git_status", "git_diff", "git_log", "git_commit",
            "send_channel_message", "list_channel_conversations", "list_channel_users",
            "acp", "acp_status",
            "systemone_ask", "systemone_noul", "systemone_choice", "systemone_score",
            "current_time", "question",
        });
    }

    [Theory, MemberData(nameof(ToolIds))]
    public void Description_ShouldOnlyBacktickToolIdsParametersOrAgentNames(string id)
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == id);
        var ownParameters = CollectParameterNames(tool.ParametersSchema);
        var allIds = BuiltInToolCatalog.All().Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var agentNames = new[] { "build", "plan", "explore", "general" };

        foreach (Match match in Regex.Matches(tool.Description, "`([^`]+)`"))
        {
            var token = match.Groups[1].Value;
            if (token.Contains('=') || token.Contains('/') || token.Contains('.') || token.Contains(' '))
                continue; // 参数值、路径、命令行片段

            var valid = allIds.Contains(token)
                || ownParameters.Contains(token)
                || agentNames.Contains(token);
            valid.Should().BeTrue($"{id} 的反引号片段 `{token}` 既不是工具 id、参数名，也不是 Agent 名");
        }
    }

    private static HashSet<string> CollectParameterNames(JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object)
            return names;

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                names.Add(property.Name);
                foreach (var nested in CollectParameterNames(property.Value))
                    names.Add(nested);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            foreach (var nested in CollectParameterNames(items))
                names.Add(nested);
        }

        return names;
    }

    private static void AssertDescribed(JsonElement schema, string id, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                prop.Value.ValueKind.Should().Be(JsonValueKind.Object,
                    $"{id} 的属性 {path}{prop.Name} 必须是对象 schema");

                var described =
                    prop.Value.TryGetProperty("description", out var d) &&
                    d.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(d.GetString());
                described.Should().BeTrue($"{id} 的属性 {path}{prop.Name} 缺少非空 description");
                Regex.IsMatch(d.GetString()!, "[\u4e00-\u9fff]").Should().BeTrue(
                    $"{id} 的属性 {path}{prop.Name} 描述必须是中文");

                AssertDescribed(prop.Value, id, $"{path}{prop.Name}.");
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var itemsDescribed =
                items.ValueKind == JsonValueKind.Object &&
                items.TryGetProperty("description", out var itemDescription) &&
                itemDescription.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(itemDescription.GetString());
            itemsDescribed.Should().BeTrue($"{id} 的 {path}[] 缺少非空 description");

            AssertDescribed(items, id, $"{path}[]");
        }

        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            var desc = schema.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : string.Empty;
            foreach (var v in values.EnumerateArray())
                desc.Should().Contain(v.GetString()!, $"{id} 的 {path} 描述必须列出枚举值");
        }
    }

    /// <summary>按 ToolExposureEngine 的规则复算目录单行：id(参数摘要): 描述（空白折叠 + 整行截断）。</summary>
    private static string BuildCatalogLine(ITool tool, int lineMax)
    {
        var schema = tool.ParametersSchema;
        var names = new List<string>();
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in req.EnumerateArray())
                {
                    if (r.GetString() is { } name)
                        required.Add(name);
                }
            }

            foreach (var p in props.EnumerateObject())
            {
                names.Add(required.Contains(p.Name) ? p.Name : p.Name + "?");
                if (names.Count == 4)
                    break;
            }
        }

        var parameterSummary = names.Count switch
        {
            0 => string.Empty,
            4 => string.Join(", ", names) + ", …",
            _ => string.Join(", ", names),
        };
        var prefix = parameterSummary.Length == 0
            ? $"{tool.Id}: "
            : $"{tool.Id}({parameterSummary}): ";
        var desc = Regex.Replace(tool.Description.Replace("\r", string.Empty), @"\s+", " ");
        var line = prefix + desc;
        return line.Length <= lineMax ? line : line[..(lineMax - 1)] + "…";
    }
}
