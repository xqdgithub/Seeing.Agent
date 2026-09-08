using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Seeing.Gateway.Client;
using Seeing.Gateway.Client.Extensions;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class GatewayClientCommonOptionsTests
{
    [Fact]
    public void DeserializeRootJson_ShouldReadAgentModelMode()
    {
        const string json = """
            {
              "WeCom": { "Enabled": true },
              "Gateway": { "BaseUrl": "http://127.0.0.1:8765" },
              "Agent": "acp-opencode",
              "Mode": "build",
              "Model": "gpt-4"
            }
            """;

        var root = JsonNode.Parse(json);
        var common = root!.Deserialize<GatewayClientCommonOptions>();

        common.Should().NotBeNull();
        common!.Agent.Should().Be("acp-opencode");
        common.Mode.Should().Be("build");
        common.Model.Should().Be("gpt-4");
    }

    [Fact]
    public void ConfigureGatewayClientCommon_ShouldBindRootAgentModelMode()
    {
        const string json = """
            {
              "Agent": "acp-opencode",
              "Mode": "build",
              "Model": "seeing-coding-plan/GLM-5"
            }
            """;

        var path = Path.Combine(Path.GetTempPath(), $"wecom-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false)
                .Build();

            var services = new ServiceCollection();
            services.ConfigureGatewayClientCommon(configuration);
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<GatewayClientCommonOptions>>().Value;

            options.Agent.Should().Be("acp-opencode");
            options.Mode.Should().Be("build");
            options.Model.Should().Be("seeing-coding-plan/GLM-5");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteCommonOptions_ShouldRemoveEmptyKeys()
    {
        var root = new JsonObject
        {
            ["Agent"] = "acp-opencode",
            ["Mode"] = "build",
            ["Model"] = "gpt-4"
        };

        WriteCommonOptions(root, agent: null, model: "new-model", mode: null);

        root.ContainsKey("Agent").Should().BeFalse();
        root.ContainsKey("Mode").Should().BeFalse();
        root["Model"]!.GetValue<string>().Should().Be("new-model");
    }

    private static void WriteCommonOptions(JsonObject root, string? agent, string? model, string? mode)
    {
        if (string.IsNullOrWhiteSpace(agent))
            root.Remove("Agent");
        else
            root["Agent"] = agent;

        if (string.IsNullOrWhiteSpace(model))
            root.Remove("Model");
        else
            root["Model"] = model;

        if (string.IsNullOrWhiteSpace(mode))
            root.Remove("Mode");
        else
            root["Mode"] = mode;
    }
}
