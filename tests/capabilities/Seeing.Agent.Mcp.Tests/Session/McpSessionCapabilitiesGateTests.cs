using FluentAssertions;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Mcp.Session;
using Xunit;

namespace Seeing.Agent.Mcp.Tests.Session;

public class McpSessionCapabilitiesGateTests
{
    [Fact]
    public async Task ListResources_When_NotDeclared_Returns_NotSupported()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, false, false, false, false, false, false, false));
        var result = await session.ListResourcesAsync();
        result.NotSupported.Should().BeTrue();
    }

    [Fact]
    public async Task ListResources_When_Declared_Returns_Items()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, true, false, false, false, false, false, false));
        var result = await session.ListResourcesAsync();
        result.NotSupported.Should().BeFalse();
        result.Items.Should().ContainSingle(r => r.Uri == "res://demo");
    }

    [Fact]
    public async Task ListPrompts_When_NotDeclared_Returns_NotSupported()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, false, false, false, false, false, false, false));
        var result = await session.ListPromptsAsync();
        result.NotSupported.Should().BeTrue();
    }

    [Fact]
    public async Task ListSkills_When_ExtensionNotDeclared_Returns_NotSupported()
    {
        var session = new FakeMcpSession(new McpServerCapabilitiesInfo(
            true, true, false, false, false, false, false, false));
        var result = await session.ListSkillsAsync();
        result.NotSupported.Should().BeTrue();
    }

    [Fact]
    public void ToolResultBuilder_EmbeddedResource_Includes_Uri_And_ReadHint()
    {
        var text = McpSessionToolResultBuilder.FormatEmbeddedResource("skill://demo/SKILL.md");
        text.Should().Contain("skill://demo/SKILL.md");
        text.Should().Contain("mcp_read_resource");
    }

    [Fact]
    public void CapabilitiesMapper_Maps_Sdk_Flags()
    {
        var sdk = new ModelContextProtocol.Protocol.ServerCapabilities
        {
            Tools = new ModelContextProtocol.Protocol.ToolsCapability(),
            Resources = new ModelContextProtocol.Protocol.ResourcesCapability
            {
                Subscribe = true,
                ListChanged = false
            },
            Prompts = new ModelContextProtocol.Protocol.PromptsCapability { ListChanged = true },
            Extensions = new Dictionary<string, object?>
            {
                ["io.modelcontextprotocol/skills"] = new Dictionary<string, object?>
                {
                    ["directoryRead"] = true
                }
            }
        };

        var info = McpSessionCapabilitiesMapper.FromSdk(sdk);
        info.Tools.Should().BeTrue();
        info.Resources.Should().BeTrue();
        info.ResourcesSubscribe.Should().BeTrue();
        info.ResourcesListChanged.Should().BeFalse();
        info.Prompts.Should().BeTrue();
        info.PromptsListChanged.Should().BeTrue();
        info.SkillsExtension.Should().BeTrue();
        info.SkillsDirectoryRead.Should().BeTrue();
    }

    /// <summary>
    /// 替身：与真实 Session 相同的 capabilities 门控契约（无能力 → NotSupported）。
    /// </summary>
    private sealed class FakeMcpSession : IMcpSession
    {
        public FakeMcpSession(McpServerCapabilitiesInfo capabilities)
        {
            Capabilities = capabilities;
        }

        public string ServerName => "fake";
        public McpServerCapabilitiesInfo Capabilities { get; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpSessionToolInfo>>(Array.Empty<McpSessionToolInfo>());

        public Task<McpSessionToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpSessionToolCallResult(false, ""));

        public Task<McpResourceListResult> ListResourcesAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Resources)
                return Task.FromResult(McpResourceListResult.Unsupported("server 未声明 resources 能力"));
            return Task.FromResult(new McpResourceListResult(
                new[] { new McpResourceInfo("res://demo", "demo") }));
        }

        public Task<McpResourceReadResult> ReadResourceAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Resources)
                return Task.FromResult(McpResourceReadResult.Unsupported("server 未声明 resources 能力"));
            return Task.FromResult(new McpResourceReadResult(
                new[] { new McpResourceContent(uri, Text: "body") }));
        }

        public Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Resources)
                return Task.FromResult(McpResourceTemplateListResult.Unsupported("server 未声明 resources 能力"));
            return Task.FromResult(new McpResourceTemplateListResult(Array.Empty<McpResourceTemplateInfo>()));
        }

        public Task<McpPromptListResult> ListPromptsAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Prompts)
                return Task.FromResult(McpPromptListResult.Unsupported("server 未声明 prompts 能力"));
            return Task.FromResult(new McpPromptListResult(Array.Empty<McpPromptInfo>()));
        }

        public Task<McpPromptGetResult> GetPromptAsync(
            string name,
            IReadOnlyDictionary<string, object?>? arguments = null,
            CancellationToken cancellationToken = default)
        {
            if (!Capabilities.Prompts)
                return Task.FromResult(McpPromptGetResult.Unsupported("server 未声明 prompts 能力"));
            return Task.FromResult(new McpPromptGetResult(Array.Empty<McpPromptMessage>()));
        }

        public Task<McpSkillExtListResult> ListSkillsAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            if (!Capabilities.SkillsExtension)
                return Task.FromResult(McpSkillExtListResult.Unsupported("server 未声明 skills 扩展"));
            return Task.FromResult(new McpSkillExtListResult(Array.Empty<McpSkillExtInfo>()));
        }

        public Task<McpSkillExtGetResult> GetSkillAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            if (!Capabilities.SkillsExtension)
                return Task.FromResult(McpSkillExtGetResult.Unsupported("server 未声明 skills 扩展"));
            return Task.FromResult(new McpSkillExtGetResult(Uri: uri, Name: "demo"));
        }
    }
}
