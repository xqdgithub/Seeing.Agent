using FluentAssertions;
using Moq;
using Seeing.Agent.Abstractions.Mcp;
using Seeing.Agent.Abstractions.Skills;
using Seeing.Agent.Mcp.Skills;
using Xunit;

namespace Seeing.Agent.Mcp.Tests;

/// <summary>
/// Task 6：MCP Skills 经贡献端口进入目录；键 mcp_{server}_{skill}；加载回源 + mcp.resource.read。
/// </summary>
public class McpSkillContributionTests
{
    private static readonly McpServerCapabilitiesInfo SkillsCaps = new(
        Tools: true,
        Resources: true,
        ResourcesSubscribe: false,
        ResourcesListChanged: false,
        Prompts: false,
        PromptsListChanged: false,
        SkillsExtension: true,
        SkillsDirectoryRead: true);

    private static readonly McpServerCapabilitiesInfo NoSkillsCaps = new(
        Tools: true,
        Resources: true,
        ResourcesSubscribe: false,
        ResourcesListChanged: false,
        Prompts: false,
        PromptsListChanged: false,
        SkillsExtension: false,
        SkillsDirectoryRead: false);

    [Fact]
    public async Task ListAsync_Connected_SkillsExtension_Builds_Sanitized_Keys()
    {
        var session = new FakeSkillSession(
            "demo.server",
            SkillsCaps,
            skills:
            [
                new McpSkillExtInfo("skill://demo/hello", "hello-world", "远端技能"),
                new McpSkillExtInfo("skill://demo/weird", "a/b c", "需净化")
            ]);

        var manager = CreateManager(("demo.server", McpConnectionState.Connected, SkillsCaps, session));
        var source = new McpSkillContributionSource(manager.Object);

        var listed = await source.ListAsync(TestContext.Current.CancellationToken);

        listed.Should().HaveCount(2);
        listed.Should().Contain(d =>
            d.Key == "mcp_demo_server_hello-world"
            && d.Description == "远端技能"
            && d.ProviderId == "mcp:demo.server");
        listed.Should().Contain(d => d.Key == "mcp_demo_server_a_b_c");
        source.SourceKind.Should().Be("mcp");
    }

    [Fact]
    public async Task ListAsync_Skips_Servers_Without_SkillsExtension()
    {
        var session = new FakeSkillSession("plain", NoSkillsCaps);
        var manager = CreateManager(("plain", McpConnectionState.Connected, NoSkillsCaps, session));
        var source = new McpSkillContributionSource(manager.Object);

        var listed = await source.ListAsync(TestContext.Current.CancellationToken);

        listed.Should().BeEmpty();
        session.ListSkillsCalls.Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_Empty_After_Disconnect()
    {
        var session = new FakeSkillSession(
            "s1",
            SkillsCaps,
            skills: [new McpSkillExtInfo("skill://s1/x", "x", "x")]);
        var manager = CreateManager(("s1", McpConnectionState.Connected, SkillsCaps, session));
        var source = new McpSkillContributionSource(manager.Object);

        (await source.ListAsync(TestContext.Current.CancellationToken)).Should().ContainSingle();

        manager.Setup(m => m.GetAllStatus()).Returns(new Dictionary<string, McpServerStatus>
        {
            ["s1"] = Status("s1", McpConnectionState.Pending, SkillsCaps)
        });
        manager.Setup(m => m.TryGetSession("s1")).Returns((IMcpSession?)null);

        (await source.ListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public void StatusChanged_Notifies_Contribution_Registry()
    {
        var session = new FakeSkillSession("s1", SkillsCaps);
        var manager = CreateManager(("s1", McpConnectionState.Connected, SkillsCaps, session));
        var registry = new Mock<ISkillContributionRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.NotifyChanged());

        var source = new McpSkillContributionSource(manager.Object, registry.Object);
        source.Attach();

        manager.Raise(
            m => m.StatusChanged += null!,
            manager.Object,
            new McpStatusChangedEventArgs(
                "s1",
                McpConnectionState.Connected,
                McpConnectionState.Pending,
                Status("s1", McpConnectionState.Pending, SkillsCaps)));

        registry.Verify(r => r.NotifyChanged(), Times.Once);
        source.Detach();
    }

    [Fact]
    public async Task LoadAsync_GetSkill_And_ReadResource_With_Authorize()
    {
        var session = new FakeSkillSession(
            "s1",
            SkillsCaps,
            skills: [new McpSkillExtInfo("skill://s1/demo", "demo", "d")],
            getSkill: uri => new McpSkillExtGetResult(
                Uri: uri,
                Name: "demo",
                AttachmentUris: ["skill://s1/refs/a.md"]),
            resources: new Dictionary<string, McpResourceContent>(StringComparer.Ordinal)
            {
                ["skill://s1/demo"] = new("skill://s1/demo", "text/markdown", Text: "# Body\nhello"),
                ["skill://s1/refs/a.md"] = new("skill://s1/refs/a.md", "text/markdown", Text: "attach")
            });

        var manager = CreateManager(("s1", McpConnectionState.Connected, SkillsCaps, session));
        var authorized = new List<string>();
        var source = new McpSkillContributionSource(
            manager.Object,
            authorizeResourceRead: (server, uri, _) =>
            {
                authorized.Add($"{server}|{uri}");
                return Task.FromResult(true);
            });

        await source.ListAsync(TestContext.Current.CancellationToken);
        var content = await source.LoadAsync("mcp_s1_demo", TestContext.Current.CancellationToken);

        content.Should().NotBeNull();
        content!.MarkdownBody.Should().Be("# Body\nhello");
        content.Attachments.Should().ContainSingle(a =>
            a.Uri == "skill://s1/refs/a.md" && a.RelativePath == "refs/a.md");
        authorized.Should().BeEquivalentTo(["s1|skill://s1/demo", "s1|skill://s1/refs/a.md"]);
        session.GetSkillCalls.Should().Be(1);
        session.ReadResourceCalls.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_FailClosed_Without_Authorizer()
    {
        var session = new FakeSkillSession(
            "s1",
            SkillsCaps,
            skills: [new McpSkillExtInfo("skill://s1/demo", "demo", "d")],
            resources: new Dictionary<string, McpResourceContent>
            {
                ["skill://s1/demo"] = new("skill://s1/demo", Text: "# Body")
            });
        var manager = CreateManager(("s1", McpConnectionState.Connected, SkillsCaps, session));
        var source = new McpSkillContributionSource(manager.Object);

        await source.ListAsync(TestContext.Current.CancellationToken);
        var content = await source.LoadAsync("mcp_s1_demo", TestContext.Current.CancellationToken);

        content.Should().BeNull();
        session.ReadResourceCalls.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_Denied_When_Authorize_Returns_False()
    {
        var session = new FakeSkillSession(
            "s1",
            SkillsCaps,
            skills: [new McpSkillExtInfo("skill://s1/demo", "demo", "d")],
            resources: new Dictionary<string, McpResourceContent>
            {
                ["skill://s1/demo"] = new("skill://s1/demo", Text: "# Body")
            });
        var manager = CreateManager(("s1", McpConnectionState.Connected, SkillsCaps, session));
        var source = new McpSkillContributionSource(
            manager.Object,
            authorizeResourceRead: (_, _, _) => Task.FromResult(false));

        await source.ListAsync(TestContext.Current.CancellationToken);
        var content = await source.LoadAsync("mcp_s1_demo", TestContext.Current.CancellationToken);

        content.Should().BeNull();
        session.ReadResourceCalls.Should().Be(0);
    }

    [Fact]
    public void SanitizeToken_Replaces_NonAlphanumeric()
    {
        McpSkillContributionSource.SanitizeToken("a.b/c d").Should().Be("a_b_c_d");
        McpSkillContributionSource.SanitizeToken("hello-world_1").Should().Be("hello-world_1");
    }

    [Fact]
    public async Task Source_Does_Not_Touch_SkillCatalog_RegisterAsync()
    {
        var catalog = new Mock<ISkillCatalog>(MockBehavior.Strict);
        var session = new FakeSkillSession(
            "s1",
            SkillsCaps,
            skills: [new McpSkillExtInfo("skill://s1/x", "x", "x")]);
        var manager = CreateManager(("s1", McpConnectionState.Connected, SkillsCaps, session));
        var source = new McpSkillContributionSource(manager.Object);

        await source.ListAsync(TestContext.Current.CancellationToken);

        catalog.Verify(
            c => c.RegisterAsync(It.IsAny<SkillInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<IMcpManager> CreateManager(
        params (string Name, McpConnectionState State, McpServerCapabilitiesInfo Caps, IMcpSession Session)[] servers)
    {
        var statuses = servers.ToDictionary(
            s => s.Name,
            s => Status(s.Name, s.State, s.Caps));

        var manager = new Mock<IMcpManager>();
        manager.Setup(m => m.GetAllStatus()).Returns(statuses);
        foreach (var s in servers)
        {
            var local = s;
            manager.Setup(m => m.TryGetSession(local.Name))
                .Returns(local.State == McpConnectionState.Connected ? local.Session : null);
            manager.Setup(m => m.GetStatus(local.Name)).Returns(statuses[local.Name]);
        }

        return manager;
    }

    private static McpServerStatus Status(
        string name,
        McpConnectionState state,
        McpServerCapabilitiesInfo caps)
        => new(
            name,
            state,
            config: null,
            McpServerPriority.Normal,
            toolCount: 0,
            toolNames: Array.Empty<string>(),
            createdAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            lastConnectedAt: null,
            lastErrorAt: null,
            lastError: null,
            reconnectAttempts: 0,
            activePolicy: null,
            capabilities: caps);

    private sealed class FakeSkillSession : IMcpSession
    {
        private readonly IReadOnlyList<McpSkillExtInfo> _skills;
        private readonly Func<string, McpSkillExtGetResult>? _getSkill;
        private readonly IReadOnlyDictionary<string, McpResourceContent> _resources;

        public FakeSkillSession(
            string serverName,
            McpServerCapabilitiesInfo capabilities,
            IReadOnlyList<McpSkillExtInfo>? skills = null,
            Func<string, McpSkillExtGetResult>? getSkill = null,
            IReadOnlyDictionary<string, McpResourceContent>? resources = null)
        {
            ServerName = serverName;
            Capabilities = capabilities;
            _skills = skills ?? [];
            _getSkill = getSkill;
            _resources = resources ?? new Dictionary<string, McpResourceContent>();
        }

        public string ServerName { get; }
        public McpServerCapabilitiesInfo Capabilities { get; }
        public int ListSkillsCalls { get; private set; }
        public int GetSkillCalls { get; private set; }
        public int ReadResourceCalls { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<McpSessionToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpSessionToolInfo>>([]);

        public Task<McpSessionToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpSessionToolCallResult(false, ""));

        public Task<McpResourceListResult> ListResourcesAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpResourceListResult([]));

        public Task<McpResourceReadResult> ReadResourceAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            ReadResourceCalls++;
            if (!Capabilities.Resources)
                return Task.FromResult(McpResourceReadResult.Unsupported());
            if (_resources.TryGetValue(uri, out var content))
                return Task.FromResult(new McpResourceReadResult([content]));
            return Task.FromResult(new McpResourceReadResult([], ErrorMessage: "missing"));
        }

        public Task<McpResourceTemplateListResult> ListResourceTemplatesAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new McpResourceTemplateListResult([]));

        public Task<McpPromptListResult> ListPromptsAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(McpPromptListResult.Unsupported());

        public Task<McpPromptGetResult> GetPromptAsync(
            string name,
            IReadOnlyDictionary<string, object?>? arguments = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(McpPromptGetResult.Unsupported());

        public Task<McpSkillExtListResult> ListSkillsAsync(
            string? cursor = null,
            CancellationToken cancellationToken = default)
        {
            ListSkillsCalls++;
            if (!Capabilities.SkillsExtension)
                return Task.FromResult(McpSkillExtListResult.Unsupported());
            return Task.FromResult(new McpSkillExtListResult(_skills));
        }

        public Task<McpSkillExtGetResult> GetSkillAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            GetSkillCalls++;
            if (!Capabilities.SkillsExtension)
                return Task.FromResult(McpSkillExtGetResult.Unsupported());
            if (_getSkill is not null)
                return Task.FromResult(_getSkill(uri));
            var hit = _skills.FirstOrDefault(s => s.Uri == uri);
            return Task.FromResult(new McpSkillExtGetResult(Uri: uri, Name: hit?.Name));
        }
    }
}
