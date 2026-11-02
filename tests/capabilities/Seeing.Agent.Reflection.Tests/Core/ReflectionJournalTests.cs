using FluentAssertions;
using Seeing.Agent.Reflection.Abstractions;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>复盘日志的落盘、覆盖、消毒与中文编码。</summary>
public sealed class ReflectionJournalTests
{
    private static string CreateTempDirectory()
        => Path.Combine(Path.GetTempPath(), "seeing-reflection-test-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    private static ReflectionRunRecord Record(
        string executionId,
        string outcome = "suggested",
        string sessionId = "session-1")
        => new(
            SessionId: sessionId,
            ExecutionId: executionId,
            Kind: ReflectionTriggerKind.Manual,
            At: DateTimeOffset.UtcNow,
            Outcome: outcome,
            CreatedSkills: new[] { "技能甲" },
            Suggestions: Array.Empty<ReflectionSuggestion>(),
            InputTokens: 100,
            OutputTokens: 50);

    [Fact]
    public async Task Journal_Should_Write_One_File_Per_Run()
    {
        var directory = CreateTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            await journal.AppendAsync(Record("exec-a"));
            await journal.AppendAsync(Record("exec-b"));

            var files = Directory.GetFiles(Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories);

            files.Should().HaveCount(2);
            files.Should().Contain(path => path.EndsWith("exec-a.json", StringComparison.Ordinal));
            files.Should().Contain(path => path.EndsWith("exec-b.json", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Journal_Should_Overwrite_Same_Execution()
    {
        var directory = CreateTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            await journal.AppendAsync(Record("exec-same", outcome: "suggested"));
            await journal.AppendAsync(Record("exec-same", outcome: "created"));

            var files = Directory.GetFiles(Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories);

            files.Should().HaveCount(1);
            File.ReadAllText(files[0]).Should().Contain("created");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Journal_Should_Sanitize_ExecutionId_Path_Traversal()
    {
        var directory = CreateTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            await journal.AppendAsync(Record("../../evil"));

            var runsDirectory = Path.Combine(directory, "runs");
            var files = Directory.GetFiles(runsDirectory, "*.json", SearchOption.AllDirectories);

            // 产物必须仍在 runs 目录内，且不能真的写到上级目录。
            files.Should().HaveCount(1);
            Path.GetFullPath(files[0])
                .Should().StartWith(Path.GetFullPath(runsDirectory));
            files[0].Should().EndWith("_evil.json");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Journal_Should_Write_Chinese_Unescaped()
    {
        var directory = CreateTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            await journal.AppendAsync(Record("exec-cn", sessionId: "会话编码测试"));

            var file = Directory.GetFiles(Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories).Single();
            var text = File.ReadAllText(file);

            text.Should().Contain("会话编码测试");
            text.Should().NotContain("\\u4f1a");
        }
        finally
        {
            TryDelete(directory);
        }
    }
}
