using System.Text.Json;
using ModelContextProtocol.Protocol;
using Seeing.Agent.Abstractions.Mcp;

namespace Seeing.Agent.Mcp.Session;

/// <summary>将 SDK <see cref="ServerCapabilities"/> 映射为状态/工具共用的摘要 DTO。</summary>
public static class McpSessionCapabilitiesMapper
{
    public const string SkillsExtensionKey = "io.modelcontextprotocol/skills";

    public static McpServerCapabilitiesInfo FromSdk(ServerCapabilities? caps)
    {
        if (caps is null)
        {
            return new McpServerCapabilitiesInfo(
                false, false, false, false, false, false, false, false);
        }

        var skillsExt = false;
        var skillsDirectoryRead = false;
        if (TryGetExtension(caps, SkillsExtensionKey, out var extValue))
        {
            skillsExt = true;
            skillsDirectoryRead = TryGetBoolProperty(extValue, "directoryRead");
        }

        return new McpServerCapabilitiesInfo(
            Tools: caps.Tools is not null,
            Resources: caps.Resources is not null,
            ResourcesSubscribe: caps.Resources?.Subscribe == true,
            ResourcesListChanged: caps.Resources?.ListChanged == true,
            Prompts: caps.Prompts is not null,
            PromptsListChanged: caps.Prompts?.ListChanged == true,
            SkillsExtension: skillsExt,
            SkillsDirectoryRead: skillsDirectoryRead);
    }

    private static bool TryGetExtension(ServerCapabilities caps, string key, out object? value)
    {
        if (caps.Extensions is not null && caps.Extensions.TryGetValue(key, out value))
            return true;

        if (caps.Experimental is not null && caps.Experimental.TryGetValue(key, out value))
            return true;

        value = null;
        return false;
    }

    private static bool TryGetBoolProperty(object? extensionValue, string propertyName)
    {
        if (extensionValue is null)
            return false;

        if (extensionValue is JsonElement je)
            return TryReadBool(je, propertyName);

        if (extensionValue is JsonDocument jd)
            return TryReadBool(jd.RootElement, propertyName);

        if (extensionValue is IDictionary<string, object?> dict
            && dict.TryGetValue(propertyName, out var raw)
            && raw is bool b)
            return b;

        if (extensionValue is IDictionary<string, object> dict2
            && dict2.TryGetValue(propertyName, out var raw2)
            && raw2 is bool b2)
            return b2;

        try
        {
            var json = JsonSerializer.SerializeToElement(extensionValue);
            return TryReadBool(json, propertyName);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;
        return prop.ValueKind == JsonValueKind.True
               || (prop.ValueKind == JsonValueKind.String
                   && bool.TryParse(prop.GetString(), out var parsed)
                   && parsed);
    }
}
