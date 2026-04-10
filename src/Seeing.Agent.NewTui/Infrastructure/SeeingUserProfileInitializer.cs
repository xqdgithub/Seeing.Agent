using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seeing.Agent.NewTui.Infrastructure;

/// <summary>
/// 首次启动时创建 ~/.seeing 目录及默认配置
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
                "apiKey": "${OPENAI_API_KEY}",
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

    private const string DefaultMcpJson = """{"mcpServers":{} }""";

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static bool EnsureCreated()
    {
        var anyNew = false;

        anyNew |= EnsureDirectory(SeeingLayout.UserSeeingDirectory);
        anyNew |= EnsureDirectory(SeeingLayout.UserSkillsDirectory);
        anyNew |= EnsureDirectory(SeeingLayout.UserRulesDirectory);

        if (!File.Exists(SeeingLayout.UserSeeingJsonPath))
        {
            WriteJsonFile(SeeingLayout.UserSeeingJsonPath, DefaultSeeingJson);
            anyNew = true;
        }

        if (!File.Exists(SeeingLayout.UserMcpJsonPath))
        {
            WriteJsonFile(SeeingLayout.UserMcpJsonPath, DefaultMcpJson);
            anyNew = true;
        }

        return anyNew;
    }

    private static bool EnsureDirectory(string path)
    {
        if (Directory.Exists(path)) return false;
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