using FluentAssertions;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Configuration;
using Xunit;

namespace Seeing.Agent.Tests.Configuration;

public class WorkspaceHistoryStoreTests
{
    [Fact]
    public void RecordOpened_ShouldPutPathFirst_AndUpdateLastInteractive()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();
        var (a, b) = dir.CreateWorkspaces("a", "b");

        store.RecordOpened(a, asDefault: false);
        store.RecordOpened(b, asDefault: true);

        var snapshot = store.Read();
        snapshot.Recent.Should().Equal(b, a);
        snapshot.LastInteractive.Should().Be(b);
    }

    [Fact]
    public void RecordOpened_WithoutDefault_ShouldKeepPreviousLastInteractive()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();
        var (a, b) = dir.CreateWorkspaces("a", "b");

        store.RecordOpened(a, asDefault: true);
        store.RecordOpened(b, asDefault: false);

        store.Read().LastInteractive.Should().Be(a);
    }

    [Fact]
    public void RecordOpened_ShouldDeduplicate_AndMoveToFront()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();
        var (a, b) = dir.CreateWorkspaces("a", "b");

        store.RecordOpened(a, false);
        store.RecordOpened(b, false);
        store.RecordOpened(a, false);

        store.Read().Recent.Should().Equal(a, b);
    }

    [Fact]
    public void Read_LegacyStringArray_ShouldBecomeRecent_AndFirstExistingBecomesDefault()
    {
        using var dir = new HistoryDir();
        var (a, b) = dir.CreateWorkspaces("a", "b");
        var missing = Path.Combine(dir.Root, "gone");
        dir.WriteRaw(System.Text.Json.JsonSerializer.Serialize(new[] { missing, b, a }));

        var snapshot = dir.CreateStore().Read();

        snapshot.Recent.Should().Equal(missing, b, a);
        snapshot.LastInteractive.Should().Be(b, "第一条仍存在的路径当作双击默认目录");
    }

    [Fact]
    public void Read_CorruptJson_ShouldReturnEmpty()
    {
        using var dir = new HistoryDir();
        dir.WriteRaw("{ not json");

        dir.CreateStore().Read().Should().BeEquivalentTo(WorkspaceHistorySnapshot.Empty);
    }

    [Fact]
    public void RecordOpened_ShouldDropMissingPaths_AndCapAtThirty()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();
        var paths = dir.CreateWorkspaces(Enumerable.Range(0, 35).Select(i => $"w{i}").ToArray());

        foreach (var path in paths)
            store.RecordOpened(path, false);

        var snapshot = store.Read();
        snapshot.Recent.Should().HaveCount(30);
        snapshot.Recent[0].Should().Be(paths[^1]);
    }

    [Fact]
    public void RecordOpened_ShouldDropMissingPath()
    {
        using var dir = new HistoryDir();
        var store = dir.CreateStore();
        var (a, _) = dir.CreateWorkspaces("a", "b");
        var missing = Path.Combine(dir.Root, "gone");

        store.RecordOpened(missing, false);
        store.RecordOpened(a, false);

        store.Read().Recent.Should().Equal(a);
    }

    [Fact]
    public void TwoStores_ShouldMergeRecent_InsteadOfOverwriting()
    {
        using var dir = new HistoryDir();
        var (a, b) = dir.CreateWorkspaces("a", "b");
        var first = dir.CreateStore();
        var second = dir.CreateStore();

        // 都先读一遍，模拟两个实例各自持有旧快照
        first.Read();
        second.Read();

        first.RecordOpened(a, true);
        second.RecordOpened(b, false);

        var merged = first.Read();
        merged.Recent.Should().Contain(a);
        merged.Recent.Should().Contain(b);
        merged.LastInteractive.Should().Be(a, "只有 asDefault 的写入改默认目录");
    }

    private sealed class HistoryDir : IDisposable
    {
        private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);

        public HistoryDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "seeing-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        private string FilePath => Path.Combine(Root, "workspace_history.json");

        public WorkspaceHistoryStore CreateStore() =>
            new(FilePath, logger: null, directoryExists: p => _existing.Contains(p));

        public (string A, string B) CreateWorkspaces(string first, string second)
        {
            var paths = CreateWorkspaces(new[] { first, second });
            return (paths[0], paths[1]);
        }

        public string[] CreateWorkspaces(params string[] names) =>
            CreateWorkspaces((IEnumerable<string>)names);

        public string[] CreateWorkspaces(IEnumerable<string> names)
        {
            var result = new List<string>();
            foreach (var name in names)
            {
                var path = Path.Combine(Root, name);
                Directory.CreateDirectory(path);
                _existing.Add(path);
                result.Add(path);
            }

            return result.ToArray();
        }

        public void WriteRaw(string content) => File.WriteAllText(FilePath, content);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
