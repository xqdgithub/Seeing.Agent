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

    [Fact]
    public async Task Journal_Should_Truncate_Long_ExecutionId_With_Hash()
    {
        // 超长 ID 必须截断（Windows 单路径段 255 上限），且截断后要补哈希：
        // 只截断会让「前缀相同」的不同 ID 指向同一文件，静默覆盖。
        var directory = CreateTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            var prefix = new string('a', ReflectionJournal.MaxFileNameLength + 50);

            await journal.AppendAsync(Record(prefix + "-one"));
            await journal.AppendAsync(Record(prefix + "-two"));

            var files = Directory.GetFiles(Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories);

            files.Should().HaveCount(2, "前缀相同的两个长 ID 必须落到两个不同文件");
            files.Should().OnlyHaveUniqueItems();

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                name.Length.Should().BeLessThanOrEqualTo(
                    ReflectionJournal.MaxFileNameLength + 9,
                    "截断到上限 + 1 个连字符 + 8 位哈希");
            }
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Failure_Should_Write_Separate_Suffix()
    {
        // 同一次执行可能既有成功记录、又有失败重试：后缀不同才不会互相覆盖。
        var directory = CreateTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            await journal.AppendAsync(Record("exec-x"));
            await journal.AppendFailureAsync(new ReflectionFailureRecord(
                SessionId: "session-1",
                ExecutionId: "exec-x",
                Kind: ReflectionTriggerKind.Manual,
                At: DateTimeOffset.UtcNow,
                SkipReason: "timeout",
                ChildSessionId: "child-1",
                Transcript: "模型输出",
                Detail: "业务超时"));

            var files = Directory.GetFiles(Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories);

            files.Should().HaveCount(2);
            files.Should().Contain(path => path.EndsWith("exec-x.json", StringComparison.Ordinal));
            files.Should().Contain(path => path.EndsWith("exec-x.fail.json", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Failure_Record_Should_Contain_Diagnostics()
    {
        // 失败会话被清理，诊断只能靠这份快照——transcript 必须真的落进去。
        var directory = CreateTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            await journal.AppendFailureAsync(new ReflectionFailureRecord(
                SessionId: "session-1",
                ExecutionId: "exec-f",
                Kind: ReflectionTriggerKind.Idle,
                At: DateTimeOffset.UtcNow,
                SkipReason: "parse-failed",
                ChildSessionId: "child-9",
                Transcript: "模型实际输出的内容",
                Detail: "子会话没有 assistant 文本"));

            var file = Directory.GetFiles(
                Path.Combine(directory, "runs"), "*.fail.json", SearchOption.AllDirectories).Single();
            var text = File.ReadAllText(file);

            text.Should().Contain("模型实际输出的内容");
            text.Should().Contain("parse-failed");
            text.Should().Contain("child-9");
        }
        finally
        {
            TryDelete(directory);
        }
    }
}
