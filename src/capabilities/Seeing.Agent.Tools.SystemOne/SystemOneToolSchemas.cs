using System.Text.Json;
using Seeing.Agent.Abstractions.SystemOne;

namespace Seeing.Agent.Core.Tools.SystemOne;

/// <summary>
/// 构建 systemone_* 工具的 JSON Schema（供 4 个工具共用）。
/// <para>
/// 入参形态：<c>state</c> 接受文本字符串或结构化对象/数组；<c>questions</c> 接受对象数组
/// 或其 JSON 字符串。实测模型/网关会把嵌套值序列化成字符串，因此解析层
/// （<see cref="SystemOneJson"/>）对两者皆做归一化，schema 同步声明联合类型。
/// </para>
/// </summary>
internal static class SystemOneToolSchemas
{
    /// <summary>按 kind 构建完整参数 Schema。</summary>
    public static JsonElement Build(SystemOneToolKind kind)
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["state"] = new Dictionary<string, object>
                {
                    ["type"] = new[] { "string", "object" },
                    ["description"] = "被评估内容。可直接给文本字符串；结构化内容给 JSON 对象/数组，或它们的 JSON 字符串（服务端会自动还原）。"
                },
                ["questions"] = new Dictionary<string, object>
                {
                    ["type"] = new[] { "array", "string" },
                    ["description"] = BuildQuestionsDescription(kind)
                },
                ["model"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "可选：覆盖 SystemOne 模型（默认用 provider 配置的 model）。"
                }
            },
            ["required"] = new[] { "state", "questions" }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    private static string BuildQuestionsDescription(SystemOneToolKind kind)
    {
        const string common =
            "questions 为**对象数组**（每项是一个问题对象）；若只能给字符串，则给该数组的 JSON 字符串，服务端会自动还原。";

        return kind switch
        {
            SystemOneToolKind.Ask =>
                common + "每项须带 type（noul|choice|score），其余字段按 type 取用：" +
                "noul→criteriaTrue/criteriaFalse 可选；choice→options（[{label,description?}]，2-50）；" +
                "score→levels（string[]，2-10，低→高）。仅用于同一份 state 上混合多题型。",
            SystemOneToolKind.Noul =>
                common + "每项为 noul 题：{id?, instructions, criteriaTrue?, criteriaFalse?}；不要含 type/options/levels。",
            SystemOneToolKind.Choice =>
                common + "每项为 choice 题：{id?, instructions, options:[{label,description?}]}（2-50）；不要含 type/levels。",
            SystemOneToolKind.Score =>
                common + "每项为 score 题：{id?, instructions, levels:[string]}（2-10，低→高）；不要含 type/options。",
            _ => common
        };
    }
}
