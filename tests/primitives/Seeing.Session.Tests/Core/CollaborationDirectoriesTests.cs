using FluentAssertions;
using Seeing.Session.Core;
using Seeing.Session.Storage;
using Seeing.Session.Tests.Management;
using System.Text.Json;
using Xunit;

namespace Seeing.Session.Tests.Core;

/// <summary>
/// 协同目录的数据层契约：Clone 深拷贝、子会话/fork/handoff 继承、旧 JSON 兼容。
/// <para>这些是"新增持久化字段"必须同步的 6 处里的可测部分；漏一处就会让继承链静默断掉。</para>
/// </summary>
public class CollaborationDirectoriesTests
{
    private const string ReportPath = @"D:\data\reports";
    private const string SharedPath = @"D:\shared\specs";

    [Fact]
    public void Clone_ShouldDeepCopyCollaborationDirectories()
    {
        var original = SessionData.Create("p1", "build");
        original.CollaborationDirectories.Add(new CollaborationDirectory(ReportPath, "报表输出"));
        original.CollaborationDirectories.Add(new CollaborationDirectory(SharedPath));

        var clone = original.Clone();

        clone.CollaborationDirectories.Should().Equal(
            new CollaborationDirectory(ReportPath, "报表输出"),
            new CollaborationDirectory(SharedPath));
        clone.CollaborationDirectories.Should().NotBeSameAs(original.CollaborationDirectories);

        // 容器独立：改写克隆不得污染原会话
        clone.CollaborationDirectories.Add(new CollaborationDirectory(@"D:\extra"));
        original.CollaborationDirectories.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateChildAsync_ShouldInheritCollaborationDirectories()
    {
        using var harness = new SessionGroupTestHarness();
        var parent = await harness.Manager.CreateRootAsync(
            "p1", "build", "code", "父", null, TestContext.Current.CancellationToken);
        parent.CollaborationDirectories.Add(new CollaborationDirectory(ReportPath, "报表输出"));
        await harness.Sessions.SaveAsync(parent.Id);

        var child = await harness.Manager.CreateChildAsync(
            parent.Id,
            "task",
            "子任务",
            Array.Empty<SessionPermissionRule>(),
            scenario: null,
            TestContext.Current.CancellationToken);

        child.CollaborationDirectories.Should().Equal(
            new CollaborationDirectory(ReportPath, "报表输出"));
        child.CollaborationDirectories.Should().NotBeSameAs(parent.CollaborationDirectories);
    }

    [Fact]
    public async Task ForkSessionAsync_ShouldInheritCollaborationDirectories()
    {
        using var harness = new SessionGroupTestHarness();
        var source = await harness.Manager.CreateRootAsync(
            "p1", "build", "code", "源", null, TestContext.Current.CancellationToken);
        source.CollaborationDirectories.Add(new CollaborationDirectory(ReportPath, "报表输出"));
        source.CollaborationDirectories.Add(new CollaborationDirectory(SharedPath));
        await harness.Sessions.SaveAsync(source.Id);

        var forked = await harness.Manager.ForkSessionAsync(
            source.Id, "分支", TestContext.Current.CancellationToken);

        forked.CollaborationDirectories.Should().Equal(
            new CollaborationDirectory(ReportPath, "报表输出"),
            new CollaborationDirectory(SharedPath));
        forked.CollaborationDirectories.Should().NotBeSameAs(source.CollaborationDirectories);
    }

    [Fact]
    public async Task CreateHandoffSuccessorAsync_ShouldInheritCollaborationDirectories()
    {
        using var harness = new SessionGroupTestHarness();
        var source = await harness.Manager.CreateRootAsync(
            "p1", "build", "code", "源", null, TestContext.Current.CancellationToken);
        source.CollaborationDirectories.Add(new CollaborationDirectory(ReportPath, "报表输出"));
        await harness.Sessions.SaveAsync(source.Id);

        var successor = await harness.Manager.CreateHandoffSuccessorAsync(
            source.Id, agent: null, title: "接续", scenario: null, TestContext.Current.CancellationToken);

        successor.CollaborationDirectories.Should().Equal(
            new CollaborationDirectory(ReportPath, "报表输出"));
        successor.CollaborationDirectories.Should().NotBeSameAs(source.CollaborationDirectories);
    }

    [Fact]
    public void OldSessionJson_MissingCollaborationField_ShouldDeserializeToEmptyList()
    {
        // 属性初始化器兜底：旧会话文件没有该字段时必须得到空列表（不能是 null）
        var data = JsonSerializer.Deserialize<SessionData>(
            """{"id":"ses_old","title":"旧会话"}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        data.Should().NotBeNull();
        data!.CollaborationDirectories.Should().NotBeNull();
        data.CollaborationDirectories.Should().BeEmpty();
    }

    [Fact]
    public async Task OldSessionJson_MissingCollaborationField_ShouldYieldEmptySummaryList()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "seeing-collab-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            // 旧版会话文件：没有 collaborationDirectories 字段
            await File.WriteAllTextAsync(
                Path.Combine(directory, "ses_old.json"),
                """{"id":"ses_old","title":"旧会话","messages":[]}""",
                TestContext.Current.CancellationToken);

            using var store = new FileSessionStore(directory);

            var summaries = new List<SessionSummary>();
            await foreach (var summary in store.ListSummariesAsync(TestContext.Current.CancellationToken))
                summaries.Add(summary);

            var only = summaries.Should().ContainSingle().Subject;
            only.Id.Should().Be("ses_old");
            only.CollaborationDirectories.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SummaryParse_WithCollaborationDirectories_ShouldProjectThem()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "seeing-collab-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, "ses_new.json"),
                """
                {
                  "id": "ses_new",
                  "title": "新会话",
                  "collaborationDirectories": [
                    { "path": "D:\\data\\reports", "note": "报表输出" },
                    { "path": "D:\\shared\\specs", "note": null }
                  ],
                  "messages": []
                }
                """,
                TestContext.Current.CancellationToken);

            using var store = new FileSessionStore(directory);

            var summaries = new List<SessionSummary>();
            await foreach (var summary in store.ListSummariesAsync(TestContext.Current.CancellationToken))
                summaries.Add(summary);

            var only = summaries.Should().ContainSingle().Subject;
            only.CollaborationDirectories.Should().Equal(
                new CollaborationDirectory(ReportPath, "报表输出"),
                new CollaborationDirectory(SharedPath, null));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
