using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};

var projectPath = @"E:\Projects\CSharp\Seeing.Agent\samples\Seeing.Agent.WebUI\.seeing\seeing.json";
var userPath = @"C:\Users\quand\.seeing\seeing.json";

var acp = new AcpOptions
{
    Enabled = true,
    DefaultBackend = "cursor",
    Backends = new Dictionary<string, AcpBackendConfig>
    {
        ["cursor"] = new() { Command = @"C:\TEST\saved.cmd", Args = new List<string> { "acp" } }
    }
};

// simulate patch
var root = JsonNode.Parse(File.ReadAllText(projectPath))!.AsObject();
var seeingAgent = root["SeeingAgent"] as JsonObject ?? new JsonObject();
seeingAgent["Acp"] = JsonSerializer.SerializeToNode(acp, jsonOptions);
root["SeeingAgent"] = seeingAgent;
File.WriteAllText(projectPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// simulate reload
var provider = new Seeing.Agent.Configuration.SeeingAgentConfigurationProvider();
provider.ReloadForWorkspace(@"E:\Projects\CSharp\Seeing.Agent\samples\Seeing.Agent.WebUI");
var cmd = provider.Options.Acp.Backends["cursor"].Command;
Console.WriteLine($"Effective command: {cmd}");

// restore original
