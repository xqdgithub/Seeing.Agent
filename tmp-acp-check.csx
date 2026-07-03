using Seeing.Agent.Configuration;

var workspace = @"E:\Projects\CSharp\Seeing.Agent\samples\Seeing.Agent.WebUI";
var provider = new SeeingAgentConfigurationProvider();
provider.ReloadForWorkspace(workspace);
var cmd = provider.Options.Acp.Backends.GetValueOrDefault("cursor")?.Command;
Console.WriteLine($"Before: {cmd}");

// simulate project override in memory then check merge manually
var userOptions = new SeeingAgentOptions();
SeeingAgentConfigurationProvider.LoadFromFile(@"C:\Users\quand\.seeing\seeing.json", userOptions, "user");
var projectOptions = new SeeingAgentOptions();
SeeingAgentConfigurationProvider.LoadFromFile(@"E:\Projects\CSharp\Seeing.Agent\samples\Seeing.Agent.WebUI\.seeing\seeing.json", projectOptions, "project");
Console.WriteLine($"User cursor: {userOptions.Acp.Backends.GetValueOrDefault("cursor")?.Command}");
Console.WriteLine($"Project cursor: {projectOptions.Acp.Backends.GetValueOrDefault("cursor")?.Command}");
Console.WriteLine($"Effective cursor: {cmd}");
