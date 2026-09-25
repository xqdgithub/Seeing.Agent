using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Core.Tools;
using Xunit;

namespace Seeing.Agent.Tests.Tools;

/// <summary>
/// ToolManager 并发加固回归：注册与启用/禁用并发执行时不得抛异常，
/// 且 tool-state.json 快照需与内存最终状态一致。
/// </summary>
public class ToolManagerConcurrencyTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), "tool-manager-concurrency-" + Guid.NewGuid().ToString("N"));

    private string UserSeeingDirectory => Path.Combine(_tempDirectory, "user", ".seeing");

    private ToolManager CreateManager()
    {
        var userSeeing = UserSeeingDirectory;
        var projectSeeing = Path.Combine(_tempDirectory, "project", ".seeing");
        Directory.CreateDirectory(userSeeing);
        Directory.CreateDirectory(projectSeeing);

        var workspace = new Mock<IWorkspaceProvider>();
        workspace.Setup(w => w.UserSeeingDirectory).Returns(userSeeing);
        workspace.Setup(w => w.ProjectSeeingDirectory).Returns(projectSeeing);

        var hooks = new HookManager(NullLogger<HookManager>.Instance);
        return new ToolManager(NullLogger<ToolManager>.Instance, hooks, workspace: workspace.Object);
    }

    [Fact]
    public async Task RegisterAndToggle_Concurrent_NoExceptionAndFileStateConsistent()
    {
        var manager = CreateManager();
        var ids = Enumerable.Range(0, 32).Select(i => $"tool_{i}").ToList();

        var tasks = ids.Select(id => Task.Run(async () =>
        {
            await manager.RegisterToolAsync(new StubTool(id));
            await manager.SetToolEnabledAsync(id, false);
            await manager.SetToolEnabledAsync(id, true);
            await manager.SetToolEnabledAsync(id, false);
        }));

        await Task.WhenAll(tasks);

        foreach (var id in ids)
            manager.IsToolEnabled(id).Should().BeFalse();

        var filePath = Path.Combine(UserSeeingDirectory, "tool-state.json");
        File.Exists(filePath).Should().BeTrue();
        var json = await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken);
        var data = JsonSerializer.Deserialize<DisabledToolsFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        data.Should().NotBeNull();
        data!.DisabledTools.Should().BeEquivalentTo(ids);
    }

    private sealed class DisabledToolsFile
    {
        public List<string>? DisabledTools { get; set; }
    }

    private sealed class StubTool(string id) : ITool
    {
        public string Id { get; } = id;
        public string Description => "stub";
        public IReadOnlyList<string> Tags => [];
        public ToolCategory Category => ToolCategory.General;
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
            => Task.FromResult(new ToolResult { Success = true, Output = "ok" });
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
