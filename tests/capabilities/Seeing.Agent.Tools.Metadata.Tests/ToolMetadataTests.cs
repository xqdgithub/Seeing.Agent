using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Configuration;
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
    public void TodoWrite_ShouldDescribeItemShape()
    {
        var tool = BuiltInToolCatalog.All().Single(t => t.Id == "todowrite");
        var items = tool.ParametersSchema.GetProperty("properties").GetProperty("todos").GetProperty("items");
        var props = items.GetProperty("properties");

        props.TryGetProperty("content", out _).Should().BeTrue();
        props.GetProperty("status").GetProperty("enum").GetArrayLength().Should().Be(5);
        props.GetProperty("priority").GetProperty("enum").GetArrayLength().Should().Be(3);
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

                AssertDescribed(prop.Value, id, $"{path}{prop.Name}.");
            }
        }

        if (schema.TryGetProperty("items", out var items))
            AssertDescribed(items, id, $"{path}[]");

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

        var prefix = names.Count == 0
            ? $"{tool.Id}: "
            : $"{tool.Id}({string.Join(", ", names)}): ";
        var desc = Regex.Replace(tool.Description.Replace("\r", string.Empty), @"\s+", " ");
        var line = prefix + desc;
        return line.Length <= lineMax ? line : line[..(lineMax - 1)] + "…";
    }
}
