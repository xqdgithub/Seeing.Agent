using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Context;
using Seeing.Agent.Core.Context.Sources;
using Seeing.Agent.Core.Instructions;
using Xunit;

namespace Seeing.Agent.Tests.Context;

/// <summary>
/// 项目指令源行为锁定：区分「不可读」与「缺失」、信封与旧渲染一致、
/// Canonical 不落正文、增量叙述只描述差异。
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
        // 与旧路径（ProjectInstructionsRenderer.Wrap）逐字节一致
        var reader = new FakeInstructionFileReader
        {
            Results =
            [
                Loaded(@"C:\work\repo\AGENTS.md", "# 根指令"),
                Loaded(@"C:\work\repo\.seeing\AGENTS.md", "# 项目指令")
            ]
        };
        var source = CreateSource(reader);

        var value = await source.ReadAsync(TestContext.Current.CancellationToken);
        var text = source.RenderInitial(value);

        var legacy = ProjectInstructionsRenderer.Wrap(
            Cwd,
            ProjectInstructions.Reasons.Initial,
            new[]
            {
                FileOf(@"C:\work\repo\AGENTS.md", "# 根指令"),
                FileOf(@"C:\work\repo\.seeing\AGENTS.md", "# 项目指令")
            }.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());

        text.Should().Be(legacy);
        text.Should().Contain("<project-instructions");
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
        entry.TryGetProperty("mtime", out _).Should().BeTrue();

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
            var reader = new InstructionFileReader(
                NullLogger<InstructionFileReader>.Instance,
                NullLoggerFactory.Instance,
                () => userProfile);

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

    private static ProjectInstructionsContextSource CreateSource(IInstructionFileReader reader) =>
        new(reader,
            () => (Cwd, WorkspaceRoot),
            NullLogger<ProjectInstructionsContextSource>.Instance);

    private static InstructionFileReadResult Loaded(string path, string content) =>
        new(path, InstructionFileReadStatus.Loaded, content, null);

    private static InstructionFileReadResult Missing(string path) =>
        new(path, InstructionFileReadStatus.Missing, null, null);

    private static InstructionFileReadResult Unreadable(string path, string error) =>
        new(path, InstructionFileReadStatus.Unreadable, null, error);

    private static InstructionFile FileOf(string path, string content) => new()
    {
        Path = path,
        Content = content,
        Hash = $"sha256:{Sha256(content)}"
    };

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
}
