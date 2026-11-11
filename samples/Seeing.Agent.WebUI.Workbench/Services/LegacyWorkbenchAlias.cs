using Seeing.Agent.Abstractions.Ui.Workbench;
using System.Text.Json;

namespace Seeing.Agent.WebUI.Workbench;

/// <summary>
/// 历史工作台 id 别名解析。
/// <para>
/// <c>markdown</c> 工作台已合并进 <c>files</c>（作为预览态）。为了让模型既有的
/// <c>open_workbench(workbenchId=markdown, payload={path})</c> 调用继续工作，
/// 在宿主侧把它解释为 <c>files</c> + <c>preview=true</c>。
/// </para>
/// <para>
/// <b>为什么不放工具侧</b>：<c>OpenWorkbenchTool</c> 的五道门闸是对声明表的通用查询。
/// 把别名塞进工具会让每道门闸各多一个特例分支，且工具拿不到「宿主怎么解释这个 id」
/// 的知识。别名是渲染层语义，归宿主表面。
/// </para>
/// </summary>
public static class LegacyWorkbenchAlias
{
    /// <summary>目标工作台 id（别名解析后）。</summary>
    public const string TargetWorkbenchId = "files";

    /// <summary>预览标志的载荷键名。</summary>
    public const string PreviewKeyName = "preview";

    /// <summary>解析别名；非别名 id 原样返回。</summary>
    /// <param name="workbenchId">请求里的工作台 id。</param>
    /// <param name="openPayload">请求里的打开载荷（可空）。</param>
    /// <returns>
    /// 解析后的 id 与载荷。别名命中时载荷被保证是对象且含 <c>preview</c>
    /// （调用方显式给了该键时保留原值）。
    /// </returns>
    public static (string WorkbenchId, JsonElement? OpenPayload) Apply(
        string workbenchId, JsonElement? openPayload)
    {
        if (!string.Equals(workbenchId, WorkbenchRequestKinds.LegacyMarkdownId, StringComparison.Ordinal))
            return (workbenchId, openPayload);

        return (TargetWorkbenchId, WithPreviewTrue(openPayload));
    }

    /// <summary>把载荷规范化为对象并补 <c>preview: true</c>（已有该键则保留原值）。</summary>
    private static JsonElement WithPreviewTrue(JsonElement? payload)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (payload is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
                properties[property.Name] = property.Value.Clone();
        }

        if (!properties.ContainsKey(PreviewKeyName))
            properties[PreviewKeyName] = JsonSerializer.SerializeToElement(true);

        return JsonSerializer.SerializeToElement(properties);
    }
}
