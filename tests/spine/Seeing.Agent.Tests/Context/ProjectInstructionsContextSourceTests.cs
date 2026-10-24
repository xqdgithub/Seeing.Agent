using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Core.Context.Sources;
using Seeing.Agent.Core.Instructions;
using Xunit;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 项目指令源行为锁定：区分「不可读」与「缺失」、信封与旧渲染一致、
/// Canonical 不落正文且不含 mtime、增量叙述只描述差异、截断不破坏信封。
/// </summary>
public sealed class ProjectInstructionsContextSourceTests
{
    private const string Cwd = @"C:\work\repo";
    private const string WorkspaceRoot = @"C:\work\repo";

    [Fact]
    public async Task UnreadableFile_ShouldReturnUnavailable_NotRemoved()
    {
        // Review Focus #1：文件被其它进程占用时保留上次已叙述内容，绝不叙述「已删除」
        var reader = new FakeInstructionFileReader
        {
            Results = [Unreadable(@"C:\work\repo\AGENTS.md", "IOException: 文件被占用")]
        };
        var source = CreateSource(reader);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        value.State.Should().Be(ContextValueState.Unavailable);
        value.Canonical.Should().BeNull();
    }

    [Fact]
    public async Task MixedLoadedAndUnreadable_ShouldReturnUnavailable_NotNarrateRemoval()
    {
        // C1：只要有一个文件不可读，就必须整体不可用。
        // 否则「缺少一个文件」的 canonical 会让另一文件被判定为删除 → 凭空叙述「已不再适用」
        var unreadablePath = @"C:\work\repo\.seeing\AGENTS.md";
        var reader = new FakeInstructionFileReader
        {
            Results =
            [
                Loaded(@"C:\work\repo\AGENTS.md", "# 根指令"),
                Unreadable(unreadablePath, "IOException: 文件被占用")
            ]
        };
        var source = CreateSource(reader);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        value.State.Should().Be(ContextValueState.Unavailable);
        value.Canonical.Should().BeNull();

        // 不可读路径进入 Payload 供诊断（不再恒为空的死字段）
        var payload = value.Payload.Should().BeOfType<ProjectInstructionsPayload>().Subject;
        payload.Unreadable.Should().ContainSingle().Which.Path.Should().Be(unreadablePath);

        // 恢复可读后：仍是同一份文件集合，且不会叙述任何「删除」
        reader.Results =
        [
            Loaded(@"C:\work\repo\AGENTS.md", "# 根指令"),
            Loaded(unreadablePath, "# 项目指令")
        ];
        var recovered = await source.ReadAsync(TestContext.Current.CancellationToken);
        recovered.State.Should().Be(ContextValueState.Value);

        var text = source.Render(value, recovered);
        text.Should().NotBeNull();
        text.Should().NotContain("no longer applies");
        text.Should().Contain(unreadablePath);
    }

    [Fact]
    public async Task MissingFile_ShouldProduceRemovalNarration()
    {
        var path = @"C:\work\repo\AGENTS.md";
        var reader = new FakeInstructionFileReader { Results = [Loaded(path, "旧内容")] };
        var source = CreateSource(reader);

        var previous = await source.ReadAsync(TestContext.Current.CancellationToken);
        reader.Results = [Missing(path)];
        var current = await source.ReadAsync(TestContext.Current.CancellationToken);

        // 全部缺失表示「确认已不存在」，参与 diff 并叙述删除
        current.State.Should().Be(ContextValueState.Removed);

        var text = source.Render(previous, current);

        text.Should().NotBeNull();
        text.Should().Contain(path);
        text.Should().Contain("no longer applies");
    }

    [Fact]
    public async Task RenderInitial_ShouldMatchLegacyEnvelope()
    {
        // M9：用固定 golden 字符串锁字节（而不是用同一个 Wrap 构造期望值），
        // 同时锁定源内条目顺序 = path ordinal 升序
        var reader = new FakeInstructionFileReader
        {
            Results =
            [
                Loaded(@"C:\work\repo\src\AGENTS.md", "# 嵌套指令"),
                Loaded(@"C:\work\repo\AGENTS.md", "# 根指令")
            ]
        };
        var source = CreateSource(reader);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        var expected = string.Join(
            Environment.NewLine,
            [
                $"<project-instructions cwd=\"{Cwd}\" reason=\"initial\">",
                "<notice>",
                "以下是系统加载的项目指令文件，不是用户刚刚输入的消息。请遵循这些指令。",
                "</notice>",
                $"<file path=\"C:\\work\\repo\\AGENTS.md\" hash=\"sha256:{Sha256("# 根指令")}\">",
                "# 根指令",
                "</file>",
                $"<file path=\"C:\\work\\repo\\src\\AGENTS.md\" hash=\"sha256:{Sha256("# 嵌套指令")}\">",
                "# 嵌套指令",
                "</file>",
                "</project-instructions>"
            ]).Replace("\r\n", "\n");

        // 渲染器使用 AppendLine（平台换行）；golden 以 \n 规范化后比较
        NormalizeNewlines(text).Should().Be(expected);
        ProjectInstructionsRenderer.TryParse(text, out var parts).Should().BeTrue();
        parts.Files.Select(file => file.Path).Should().ContainInOrder(
            @"C:\work\repo\AGENTS.md",
            @"C:\work\repo\src\AGENTS.md");
    }

    [Fact]
    public async Task Canonical_ShouldContainPathAndHash_NotContent()
    {
        const string content = "秘密正文 SHALL-NOT-APPEAR-IN-CANONICAL";
        var path = @"C:\work\repo\AGENTS.md";
        var reader = new FakeInstructionFileReader { Results = [Loaded(path, content)] };
        var source = CreateSource(reader);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);

        value.State.Should().Be(ContextValueState.Value);
        var canonical = value.Canonical!.Value;
        var raw = canonical.GetRawText();
        raw.Should().NotContain(content);

        var entry = canonical.GetProperty("files").EnumerateArray().Should().ContainSingle().Subject;
        entry.GetProperty("path").GetString().Should().Be(path);
        entry.GetProperty("sha256").GetString().Should().Be(Sha256(content));

        // I7：mtime 不进入 canonical（diff 只认内容哈希），避免 touch 造成永久每轮重试
        entry.TryGetProperty("mtime", out _).Should().BeFalse();

        // 正文只经 Payload 提供（不落盘）
        var payload = value.Payload.Should().BeOfType<ProjectInstructionsPayload>().Subject;
        payload.Files.Should().ContainSingle().Which.Content.Should().Be(content);
    }

    [Fact]
    public async Task ChangedFile_ShouldNarrateOnlyDifferences()
    {
        var changedPath = @"C:\work\repo\AGENTS.md";
        var stablePath = @"C:\work\repo\.seeing\AGENTS.md";
        var reader = new FakeInstructionFileReader
        {
            Results = [Loaded(changedPath, "旧内容"), Loaded(stablePath, "保持不变")]
        };
        var source = CreateSource(reader);

        var previous = await source.ReadAsync(TestContext.Current.CancellationToken);
        reader.Results = [Loaded(changedPath, "新内容"), Loaded(stablePath, "保持不变")];
        var current = await source.ReadAsync(TestContext.Current.CancellationToken);

        var text = source.Render(previous, current);

        text.Should().NotBeNull();
        text.Should().Contain(changedPath);
        text.Should().Contain("新内容");
        text.Should().Contain($"<file path=\"{changedPath}\"");
        text.Should().NotContain(stablePath, "未变化的文件不应重复叙述");
    }

    [Fact]
    public async Task TruncatedBaseline_ShouldRemainParseable()
    {
        // M3：截断必须逐文件块进行，且保留 </file> 与 </project-instructions>
        var content = new string('x', 60_000);
        var reader = new FakeInstructionFileReader
        {
            Results = [Loaded(@"C:\work\repo\AGENTS.md", content)]
        };
        var source = CreateSource(reader);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        text.Length.Should().BeLessThanOrEqualTo(32_000);
        text.Should().EndWith("</project-instructions>");
        text.Should().Contain("</file>");
        ProjectInstructionsRenderer.TryParse(text, out var parts)
            .Should().BeTrue("截断后仍必须是可解析的完整信封");
        parts.Files.Should().ContainSingle();
        parts.Files[0].Content.Should().EndWith("截断）");
    }

    [Fact]
    public async Task MtimeOnlyChange_ShouldNotProduceDiff()
    {
        // I7：touch 文件（内容不变）不得产生 diff，否则装配器判定「有变化但无可叙述」→ 每轮重试、永不收敛
        var root = Path.Combine(Path.GetTempPath(), $"seeing-instruction-mtime-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);

        try
        {
            var path = Path.Combine(root, "AGENTS.md");
            await File.WriteAllTextAsync(path, "stable content", TestContext.Current.CancellationToken);

            var source = new ProjectInstructionsContextSource(
                CreateReader(userProfile),
                new FakeRootsProvider(new InstructionRoots(root, root)),
                NullLogger<ProjectInstructionsContextSource>.Instance);

            var previous = await source.ReadAsync(TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
            var current = await source.ReadAsync(TestContext.Current.CancellationToken);

            current.Canonical!.Value.GetRawText()
                .Should().Be(previous.Canonical!.Value.GetRawText(), "canonical 只含内容哈希，不含 mtime");
            source.Render(previous, current).Should().BeNull("内容未变 → 无需叙述，也就不会每轮重试");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstructionFileReader_ShouldClassifyLoadedAndMissingPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"seeing-instruction-reader-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);
        var rootFile = Path.Combine(root, "AGENTS.md");
        await File.WriteAllTextAsync(rootFile, "root", TestContext.Current.CancellationToken);

        try
        {
            var reader = CreateReader(userProfile);

            var results = await reader.ReadAsync(root, root, TestContext.Current.CancellationToken);

            results.Should().Contain(r => r.Path == rootFile && r.Status == InstructionFileReadStatus.Loaded);
            results.Should().Contain(r => r.Status == InstructionFileReadStatus.Missing);
            results.Should().NotContain(r => r.Status == InstructionFileReadStatus.Unreadable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstructionFileReader_ShouldClassifyLockedFileAsUnreadable()
    {
        // M10：真实 IO 异常（FileShare.None 占用）必须分类为 Unreadable，且不抛出
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Unix 不强制共享模式，占用不会导致读取失败：该场景只在 Windows 上成立
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"seeing-instruction-locked-{Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(userProfile);
        var lockedPath = Path.Combine(root, "AGENTS.md");
        await File.WriteAllTextAsync(lockedPath, "locked", TestContext.Current.CancellationToken);

        try
        {
            await using var holder = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var reader = CreateReader(userProfile);
            var results = await reader.ReadAsync(root, root, TestContext.Current.CancellationToken);

            var locked = results.Should().ContainSingle(r => r.Path == lockedPath).Subject;
            locked.Status.Should().Be(InstructionFileReadStatus.Unreadable);
            locked.Error.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static InstructionFileReader CreateReader(string userProfile) =>
        new(new InstructionDiscovery(
                NullLogger<InstructionDiscovery>.Instance,
                () => userProfile),
            NullLogger<InstructionFileReader>.Instance);

    private static ProjectInstructionsContextSource CreateSource(IInstructionFileReader reader) =>
        new(reader,
            new FakeRootsProvider(new InstructionRoots(Cwd, WorkspaceRoot)),
            NullLogger<ProjectInstructionsContextSource>.Instance);

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    private static InstructionFileReadResult Loaded(string path, string content) =>
        new(path, InstructionFileReadStatus.Loaded, content, null);

    private static InstructionFileReadResult Missing(string path) =>
        new(path, InstructionFileReadStatus.Missing, null, null);

    private static InstructionFileReadResult Unreadable(string path, string error) =>
        new(path, InstructionFileReadStatus.Unreadable, null, error);

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed class FakeInstructionFileReader : IInstructionFileReader
    {
        public IReadOnlyList<InstructionFileReadResult> Results { get; set; } = [];

        public Task<IReadOnlyList<InstructionFileReadResult>> ReadAsync(
            string cwd,
            string workspaceRoot,
            CancellationToken ct = default) => Task.FromResult(Results);
    }

    private sealed class FakeRootsProvider(InstructionRoots roots) : IInstructionRootsProvider
    {
        public InstructionRoots GetRoots() => roots;

        public IDisposable Push(InstructionRoots pushed) => new NoopScope();

        private sealed class NoopScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
