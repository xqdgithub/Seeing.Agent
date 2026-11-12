using FluentAssertions;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Configuration;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>
/// 复盘产物目录跟随工作区切换。
/// <para>
/// 原先目录在 DI 工厂里一次性算好并存成不可变值：切到工作区 B 之后
/// journal / 账本 / 记录页仍读写 A 的 <c>.seeing/reflection</c>——
/// 新工作区的复盘记录永远看不到，预算也记在旧项目上。
/// 会话存储有 <c>SessionReloadHandler</c> 做同样的事，这里是它的复盘版。
/// </para>
/// </summary>
public sealed class ReflectionStorageRootTests
{
    private static readonly string s_rootA = Path.Combine(Path.GetTempPath(), "ws-a");
    private static readonly string s_rootB = Path.Combine(Path.GetTempPath(), "ws-b");

    [Fact]
    public void AlignToWorkspace_Should_Switch_Path_When_Workspace_Changes()
    {
        var workspace = new FakeWorkspace(s_rootA);
        var root = new ReflectionStorageRoot(Path.Combine(s_rootA, ".seeing", "reflection"));

        workspace.SwitchTo(s_rootB);
        root.AlignToWorkspace(workspace).Should().BeTrue();

        root.Path.Should().Be(Path.Combine(s_rootB, ".seeing", "reflection"));
    }

    [Fact]
    public void AlignToWorkspace_Should_Be_Idempotent()
    {
        var workspace = new FakeWorkspace(s_rootA);
        var root = new ReflectionStorageRoot(Path.Combine(s_rootA, ".seeing", "reflection"));

        root.AlignToWorkspace(workspace).Should().BeFalse("路径没变就不该报告变更");
    }

    [Fact]
    public void AlignToWorkspace_Should_Keep_Path_When_Workspace_Unresolved()
    {
        // 有界面宿主停在首页是合法状态：此时不能把占位目录当真实工作区钉死。
        var workspace = new FakeWorkspace(null);
        var fallback = ReflectionStorageRoot.TempFallback;
        var root = new ReflectionStorageRoot(fallback);

        root.AlignToWorkspace(workspace).Should().BeFalse();
        root.Path.Should().Be(fallback);
    }

    [Fact]
    public void Journal_Should_Write_Into_New_Workspace_After_Switch()
    {
        // 端到端：持有者换根后，journal 必须写进新根而不是继续写旧根。
        var directoryA = Path.Combine(Path.GetTempPath(), "seeing-reflect-a-" + Guid.NewGuid().ToString("N"));
        var directoryB = Path.Combine(Path.GetTempPath(), "seeing-reflect-b-" + Guid.NewGuid().ToString("N"));

        try
        {
            var root = new ReflectionStorageRoot(directoryA);
            var journal = new ReflectionJournal(root);

            journal.AppendAsync(Record("exec-a")).GetAwaiter().GetResult();

            root.SetPath(directoryB).Should().BeTrue();
            journal.AppendAsync(Record("exec-b")).GetAwaiter().GetResult();

            Directory.GetFiles(Path.Combine(directoryA, "runs"), "*.json", SearchOption.AllDirectories)
                .Should().ContainSingle(p => p.EndsWith("exec-a.json", StringComparison.Ordinal));
            Directory.GetFiles(Path.Combine(directoryB, "runs"), "*.json", SearchOption.AllDirectories)
                .Should().ContainSingle(p => p.EndsWith("exec-b.json", StringComparison.Ordinal),
                    "切换后新记录必须落到新工作区");
        }
        finally
        {
            TryDelete(directoryA);
            TryDelete(directoryB);
        }
    }

    [Fact]
    public void Ledger_Should_Reset_On_Directory_Change()
    {
        // 预算按项目分别计：切到新工作区必须重新读盘，不能沿用旧工作区的累计。
        var directoryA = Path.Combine(Path.GetTempPath(), "seeing-ledger-a-" + Guid.NewGuid().ToString("N"));
        var directoryB = Path.Combine(Path.GetTempPath(), "seeing-ledger-b-" + Guid.NewGuid().ToString("N"));

        try
        {
            var root = new ReflectionStorageRoot(directoryA);
            var ledger = new ExecutionUsageLedger(root);

            ledger.Add(new Seeing.Agent.Abstractions.Llm.TokenUsage { InputTokens = 100, OutputTokens = 0 });
            ledger.UsedToday().Should().Be(100);

            root.SetPath(directoryB);

            ledger.UsedToday().Should().Be(0, "新工作区没有历史用量，不能继承旧项目的账本");
        }
        finally
        {
            TryDelete(directoryA);
            TryDelete(directoryB);
        }
    }

    private static Reflection.Abstractions.ReflectionRunRecord Record(string executionId)
        => new(
            SessionId: "ses-1",
            ExecutionId: executionId,
            Kind: ReflectionTriggerKind.Manual,
            At: DateTimeOffset.UtcNow,
            Outcome: ReflectionRunOutcomes.Suggested,
            CreatedSkills: Array.Empty<string>(),
            Suggestions: Array.Empty<ReflectionSuggestion>(),
            InputTokens: 1,
            OutputTokens: 1);

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    private sealed class FakeWorkspace(string? root) : IWorkspaceProvider
    {
        private string? _root = root;

        public void SwitchTo(string newRoot) => _root = newRoot;

        public bool IsResolved => _root is not null;

        public string? ResolvedRoot => _root;

        public string UserSeeingDirectory => Path.Combine(Path.GetTempPath(), "seeing-user");

        public string ProjectSeeingDirectory => Path.Combine(_root ?? Path.GetTempPath(), ".seeing");

        public string StartupDirectory => Path.GetTempPath();

        public WorkspaceResolutionSource ResolutionSource => WorkspaceResolutionSource.ManualSwitch;

        public string? GlobalWorkspaceRoot => null;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceRootChanged;

        public void SetInitialWorkspace(string workspaceRoot, WorkspaceResolutionSource source)
        {
            var old = _root ?? string.Empty;
            _root = workspaceRoot;
            WorkspaceRootChanged?.Invoke(this, new WorkspaceChangedEventArgs
            {
                OldWorkspace = old,
                NewWorkspace = workspaceRoot
            });
        }

        public void SetWorkspaceRoot(string workspaceRoot) => SetInitialWorkspace(
            workspaceRoot, WorkspaceResolutionSource.ManualSwitch);

        public string GetSeeingDirectory(ConfigLevel level) => level == ConfigLevel.User
            ? UserSeeingDirectory
            : ProjectSeeingDirectory;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetGlobalWorkspaceRootAsync(string? path, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetWorkspaceOptionsAsync(WorkspaceOptions options, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetProjectRoot() => _root ?? throw new InvalidOperationException("未选定");
    }
}
