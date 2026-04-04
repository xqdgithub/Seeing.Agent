using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seeing.Agent.Tui.Infrastructure;

/// <summary>
/// 首次启动时在用户目录创建 <c>~/.seeing</c> 及约定的子目录与默认配置文件（不覆盖已有文件）。
/// </summary>
public static class SeeingUserProfileInitializer
{
    private const string DefaultSeeingJson = """
        {
          "SeeingAgent": {
            "DefaultModel": "gpt-4o",
            "DefaultProvider": "openai",
            "DefaultAgent": "primary",
            "Plugins": [],
            "SkillPaths": [],
            "Providers": {
              "openai": {
                "id": "openai",
                "type": "OpenAI",
                "baseURL": "https://api.openai.com/v1",
                "apiKey":"YOUR_API_KEY"
                "timeout": 600000,
                "max_retries": 3
              }
            },
            "Agents": {
              "primary": {
                "Provider": "openai",
                "Model": "gpt-4o",
                "SystemPrompt": "You are a helpful assistant running in Seeing.Agent TUI.",
                "MaxSteps": 32,
                "Temperature": 0.2
              }
            }
          }
        }
        """;

    private const string DefaultMcpJson = """{"mcpServers":{}}""";

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>
    /// 确保目录存在；缺失时创建默认 <c>seeing.json</c>、<c>mcp.json</c>。
    /// </summary>
    /// <returns>是否新创建了任一项（用于向用户提示）。</returns>
    public static bool EnsureCreated()
    {
        var anyNew = false;

        anyNew |= EnsureDirectory(SeeingLayout.UserSeeingDirectory);
        anyNew |= EnsureDirectory(SeeingLayout.UserSkillsDirectory);
        anyNew |= EnsureDirectory(SeeingLayout.UserRulesDirectory);

        var seeingPath = SeeingLayout.UserSeeingJsonPath;
        if (!File.Exists(seeingPath))
        {
            WriteJsonFile(seeingPath, DefaultSeeingJson);
            anyNew = true;
        }

        var mcpPath = SeeingLayout.UserMcpJsonPath;
        if (!File.Exists(mcpPath))
        {
            WriteJsonFile(mcpPath, DefaultMcpJson);
            anyNew = true;
        }

        return anyNew;
    }

    private static bool EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
            return false;
        Directory.CreateDirectory(path);
        return true;
    }

    private static void WriteJsonFile(string path, string json)
    {
        var node = JsonNode.Parse(json.Trim());
        var text = node!.ToJsonString(PrettyJson);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
