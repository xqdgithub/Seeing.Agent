using Seeing.Agent.Abstractions.Configuration;
using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Cli.Infrastructure;
using Seeing.Agent.Configuration;

namespace Seeing.Agent.Cli.Commands;

public static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", "管理配置");

        var showSectionArg = new Argument<string?>("section") { DefaultValueFactory = _ => null, Description = "要显示的配置段（不指定则显示全部）" };
        var showCommand = new Command("show", "查看配置") { showSectionArg };
        showCommand.SetAction(async parseResult =>
        {
            var section = parseResult.GetValue<string?>(showSectionArg);
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var config = host.Services.GetRequiredService<UnifiedConfigManager>();
            await ShowConfig(config, section);
        });

        var setPathArg = new Argument<string>("path") { Description = "配置路径（如 Gateway.Port）" };
        var setValueArg = new Argument<string>("value") { Description = "配置值" };
        var setCommand = new Command("set", "设置配置值") { setPathArg, setValueArg };
        setCommand.SetAction(async parseResult =>
        {
            var path = parseResult.GetValue<string>(setPathArg);
            var value = parseResult.GetValue<string>(setValueArg);
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var config = host.Services.GetRequiredService<UnifiedConfigManager>();
            await SetConfig(config, path, value);
        });

        var initCommand = new Command("init", "初始化工作区");
        initCommand.SetAction(async parseResult =>
        {
            using var host = await CliServiceBootstrap.BuildHostAsync(Array.Empty<string>());
            var config = host.Services.GetRequiredService<UnifiedConfigManager>();
            await InitWorkspace(config);
        });

        command.Subcommands.Add(showCommand);
        command.Subcommands.Add(setCommand);
        command.Subcommands.Add(initCommand);

        return command;
    }

    private static Task ShowConfig(UnifiedConfigManager config, string? section)
    {
        if (section != null)
        {
            var info = config.GetSourceInfo(section);
            if (info == null || !info.HasProjectLevel && !info.HasUserLevel)
            {
                Console.WriteLine($"未找到配置段: {section}");
                return Task.CompletedTask;
            }

            Console.WriteLine($"[{section}]");
            Console.WriteLine($"  来源: {info.SourceDescription}");
            Console.WriteLine($"  范围: {info.Scope}");

            if (info.Scope != ConfigScope.ProjectOnly)
                Console.WriteLine($"  用户级: {info.UserPath ?? "(无)"}");
            Console.WriteLine($"  项目级: {info.ProjectPath}");
        }
        else
        {
            var sections = config.GetAllSections();
            foreach (var (name, meta) in sections.OrderBy(s => s.Value.DisplayOrder))
            {
                var info = config.GetSourceInfo(name);
                Console.WriteLine($"[{name}] ({info.SourceDescription})");
            }
        }

        return Task.CompletedTask;
    }

    private static async Task SetConfig(UnifiedConfigManager config, string path, string value)
    {
        // Parse: "DefaultAgent" → sectionName only, "Gateway.Port" → sectionName + key
        var dotIndex = path.IndexOf('.');
        string sectionName;
        string? key;

        if (dotIndex < 0)
        {
            sectionName = path;
            key = null;
        }
        else
        {
            sectionName = path[..dotIndex];
            key = path[(dotIndex + 1)..];
        }

        var meta = config.GetSectionMeta(sectionName);
        if (meta == null)
        {
            Console.Error.WriteLine($"错误: 未注册的配置段 '{sectionName}'");
            Environment.ExitCode = 1;
            return;
        }

        var level = ConfigLevel.Project;
        var raw = await config.GetRawJsonAsync(level, meta.FileName);
        var rootProp = meta.FileName == "seeing.json" ? "SeeingAgent" : null;

        var updatedJson = key != null
            ? SetNestedValue(raw, rootProp, sectionName, key, value)
            : SetSimpleValue(raw, rootProp, sectionName, value);

        await config.SaveRawJsonAsync(level, meta.FileName, updatedJson);
        Console.WriteLine($"已设置 {path} = {value}");
    }

    private static string SetSimpleValue(string rawJson, string? rootProperty, string section, string newValue)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
        System.Text.Json.Nodes.JsonNode target;

        if (rootProperty != null)
        {
            var root = doc?.AsObject();
            if (root == null) return rawJson;
            if (!root.ContainsKey(rootProperty))
                root[rootProperty] = new System.Text.Json.Nodes.JsonObject();
            target = root[rootProperty]!;
        }
        else
        {
            target = doc!;
        }

        var targetObj = target.AsObject();

        // Set the simple value at the root level
        if (int.TryParse(newValue, out var intVal))
            targetObj[section] = intVal;
        else if (bool.TryParse(newValue, out var boolVal))
            targetObj[section] = boolVal;
        else
            targetObj[section] = newValue;

        return doc?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? rawJson;
    }

    private static string SetNestedValue(string rawJson, string? rootProperty, string section, string key, string newValue)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(rawJson);
        System.Text.Json.Nodes.JsonNode target;

        if (rootProperty != null)
        {
            var root = doc?.AsObject();
            if (root == null) return rawJson;
            if (!root.ContainsKey(rootProperty))
                root[rootProperty] = new System.Text.Json.Nodes.JsonObject();
            target = root[rootProperty]!;
        }
        else
        {
            target = doc!;
        }

        var sectionNode = target.AsObject();
        if (!sectionNode.ContainsKey(section))
            sectionNode[section] = new System.Text.Json.Nodes.JsonObject();

        var sectionObj = sectionNode[section]?.AsObject();
        if (sectionObj != null)
        {
            if (int.TryParse(newValue, out var intVal))
                sectionObj[key] = intVal;
            else if (bool.TryParse(newValue, out var boolVal))
                sectionObj[key] = boolVal;
            else
                sectionObj[key] = newValue;
        }

        return doc?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? rawJson;
    }

    private static async Task InitWorkspace(UnifiedConfigManager config)
    {
        var workspaceRoot = Environment.GetEnvironmentVariable("SEEING_WORKSPACE_ROOT")
            ?? Directory.GetCurrentDirectory();

        var seeingDir = Path.Combine(workspaceRoot, ".seeing");
        var agentsDir = Path.Combine(seeingDir, "agents");
        var sessionsDir = Path.Combine(seeingDir, "sessions");

        Directory.CreateDirectory(agentsDir);
        Directory.CreateDirectory(sessionsDir);

        var seeingJson = Path.Combine(seeingDir, "seeing.json");
        if (!File.Exists(seeingJson))
        {
            var template = @"{
  ""SeeingAgent"": {
    ""Gateway"": {
      ""Enabled"": true,
      ""Port"": 8765,
      ""BindAddress"": ""127.0.0.1""
    }
  }
}";
            await File.WriteAllTextAsync(seeingJson, template);
        }

        Console.WriteLine($"工作区已初始化: {seeingDir}");
        Console.WriteLine($"  {seeingJson}");
        Console.WriteLine($"  {agentsDir}/");
        Console.WriteLine($"  {sessionsDir}/");
    }
}
