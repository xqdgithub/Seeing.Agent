using FluentAssertions;
using Seeing.Agent.Reflection.Core;
using Seeing.Agent.Reflection.Ui;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Ui;

/// <summary>
/// 记录页写操作的边界与语义（按稳定 <c>RunId</c> 定位）。
/// <para>
/// 这里钉住三件曾经不成立的事：
/// </para>
/// <list type="number">
/// <item>同一段空闲/手动触发的记录<b>不再互相覆盖</b>（旧实现用空 ExecutionId → <c>unknown.json</c>）；</item>
/// <item>成功记录与失败快照共享同一 <c>RunId</c>，因此能一起删；</item>
/// <item>删除按 ID 而非路径，越界标识一律拒绝。</item>
/// </list>
/// </summary>
public sealed class ReflectionRunOperationsTests
{
    private static string NewTempDirectory()
        => Path.Combine(Path.GetTempPath(), "seeing-refl-ops-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task Records_With_Empty_ExecutionId_Should_Not_Overwrite_Each_Other()
    {
        // 这是最要紧的一条：空闲/手动触发的 ExecutionId 是空串，
        // 旧实现消毒后得到 "unknown"，同一天多条记录写进同一文件，只剩最后一条。
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            await journal.AppendAsync(Record("run_a", executionId: ""));
            await journal.AppendAsync(Record("run_b", executionId: ""));
            await journal.AppendAsync(Record("run_c", executionId: ""));

            var files = Directory.GetFiles(
                Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories);

            files.Should().HaveCount(3, "每条复盘都必须独立成文件，覆盖就是数据丢失");
            files.Should().NotContain(p => Path.GetFileName(p).StartsWith("unknown"),
                "空 ExecutionId 不得退化成共享的 unknown 文件名");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Delete_Should_Remove_Record_By_RunId()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_a"));

            var deleted = await journal.DeleteAsync("run_a");

            deleted.Should().Be(1);
            Directory.GetFiles(Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories)
                .Should().BeEmpty();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Delete_Should_Also_Remove_Failure_Companion_Sharing_RunId()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_b"));
            await journal.AppendFailureAsync(FailureRecord("run_b"));

            (await journal.DeleteAsync("run_b")).Should().Be(2,
                "成功记录与失败快照共享 RunId，必须一起删；否则残留孤立快照，用户以为没删掉");
            Directory.GetFiles(Path.Combine(directory, "runs"), "*.json", SearchOption.AllDirectories)
                .Should().BeEmpty();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Delete_Should_Find_Record_Regardless_Of_Day_Directory()
    {
        // 记录按天分目录，但 RunId 与日期无绑定：删除不能只猜「今天」。
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_old") with { At = DateTimeOffset.UtcNow.AddDays(-30) });

            (await journal.DeleteAsync("run_old")).Should().Be(1);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Delete_Should_Reject_Traversal_In_RunId()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_keep"));

            // 消毒会把分隔符换掉，因此这里验证的是「不会误删到相邻文件」而不是抛异常。
            (await journal.DeleteAsync("../../evil")).Should().Be(0);
            (await journal.DeleteAsync("run_keep")).Should().Be(1);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Delete_Should_Return_Zero_For_Unknown_RunId()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);

            (await journal.DeleteAsync("run_missing")).Should().Be(0);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Reader_Should_Find_By_RunId()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_find"));

            var found = await new ReflectionRunReader(directory).FindByRunIdAsync("run_find");

            found.Should().NotBeNull();
            found!.RunId.Should().Be("run_find");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Reader_Should_Reject_Path_Outside_Runs_Root()
    {
        var directory = NewTempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var outside = Path.Combine(directory, "secret.json");
            await File.WriteAllTextAsync(outside, "{}", TestContext.Current.CancellationToken);

            (await new ReflectionRunReader(directory).FindAsync(outside)).Should().BeNull();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Legacy_Record_Without_RunId_Should_Fall_Back_To_FileName()
    {
        // 升级前的记录里没有 RunId 字段。若不回退，这些记录将无法按 ID 删除。
        var directory = NewTempDirectory();
        try
        {
            var dayDirectory = Path.Combine(directory, "runs", "20260101");
            Directory.CreateDirectory(dayDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(dayDirectory, "exec_legacy.json"),
                """
                {
                  "SessionId": "ses-1",
                  "ExecutionId": "exec_legacy",
                  "Kind": "TurnNudge",
                  "At": "2026-01-01T00:00:00+00:00",
                  "Outcome": "suggested",
                  "CreatedSkills": [],
                  "Suggestions": [],
                  "InputTokens": 1,
                  "OutputTokens": 1
                }
                """,
                TestContext.Current.CancellationToken);

            var found = await new ReflectionRunReader(directory).FindByRunIdAsync("exec_legacy");

            found.Should().NotBeNull("旧记录必须能从文件名回退出身份，否则永远删不掉");
            found!.RunId.Should().Be("exec_legacy");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task Operations_Should_Report_Failure_When_Record_Missing()
    {
        var directory = NewTempDirectory();
        try
        {
            var operations = new ReflectionRunOperations(
                new ReflectionJournal(directory),
                new ReflectionRunReader(directory),
                NewWriter());

            var result = await operations.DeleteAsync("run_missing");

            result.Success.Should().BeFalse();
            result.Error.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task CreateSkill_Should_Reject_Out_Of_Range_Index()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_g"));

            var operations = new ReflectionRunOperations(
                journal, new ReflectionRunReader(directory), NewWriter());

            var result = await operations.CreateSkillAsync(
                "run_g", suggestionIndex: 5, Seeing.Agent.Abstractions.Configuration.ConfigLevel.Project);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("越界");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task CreateSkill_Should_Skip_Mode_Gate()
    {
        // SuggestOnly（默认）下自动写盘是被禁止的，但用户显式点「生成 SKILL」必须能写进去。
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_h", withSuggestion: true));

            var suggestions = new ReflectionSuggestionsWriter(
                new FixedOptionsMonitor(new Seeing.Agent.Reflection.Configuration.ReflectionOptions
                {
                    Mode = Seeing.Agent.Reflection.Configuration.ReflectionMode.SuggestOnly
                }),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionSuggestionsWriter>.Instance,
                new RecordingSkillWriter(),
                catalog: null);

            var operations = new ReflectionRunOperations(
                journal, new ReflectionRunReader(directory), suggestions);

            var result = await operations.CreateSkillAsync(
                "run_h", 0, Seeing.Agent.Abstractions.Configuration.ConfigLevel.Project);

            result.Success.Should().BeTrue("手动生成是显式动作，不受「自动写盘」的模式限制");
            result.SkillName.Should().Be("build-before-edit");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task CreateSkill_Should_Write_Project_Level_By_Default()
    {
        // 级别决策：复盘结论来自本项目会话，因此默认落项目级。
        // 弹窗可改用户级；跨项目复用仍可走技能页晋升（见 docs/architecture/25 §11.4）。
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_i", withSuggestion: true));

            var writer = new RecordingSkillWriter();
            var operations = new ReflectionRunOperations(
                journal,
                new ReflectionRunReader(directory),
                new ReflectionSuggestionsWriter(
                    new FixedOptionsMonitor(new Seeing.Agent.Reflection.Configuration.ReflectionOptions()),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionSuggestionsWriter>.Instance,
                    writer,
                    catalog: null));

            await operations.CreateSkillAsync(
                "run_i", 0, Seeing.Agent.Abstractions.Configuration.ConfigLevel.Project);

            writer.LastLevel.Should().Be(Seeing.Agent.Abstractions.Configuration.ConfigLevel.Project);
            writer.LastName.Should().Be("build-before-edit");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task CreateSkill_Should_Reject_Chinese_Target_Without_Draft()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_zh", chineseTarget: true));

            var writer = new RecordingSkillWriter();
            var operations = new ReflectionRunOperations(
                journal,
                new ReflectionRunReader(directory),
                new ReflectionSuggestionsWriter(
                    new FixedOptionsMonitor(new Seeing.Agent.Reflection.Configuration.ReflectionOptions()),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionSuggestionsWriter>.Instance,
                    writer,
                    catalog: null));

            var result = await operations.CreateSkillAsync(
                "run_zh", 0, Seeing.Agent.Abstractions.Configuration.ConfigLevel.Project);

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("小写字母");
            writer.LastName.Should().BeNull();
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task CreateSkill_Should_Honour_Draft_Override()
    {
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_draft", chineseTarget: true));

            var writer = new RecordingSkillWriter();
            var operations = new ReflectionRunOperations(
                journal,
                new ReflectionRunReader(directory),
                new ReflectionSuggestionsWriter(
                    new FixedOptionsMonitor(new Seeing.Agent.Reflection.Configuration.ReflectionOptions()),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionSuggestionsWriter>.Instance,
                    writer,
                    catalog: null));

            var result = await operations.CreateSkillAsync(
                "run_draft",
                0,
                Seeing.Agent.Abstractions.Configuration.ConfigLevel.User,
                new ReflectionSkillDraft(
                    "build-before-edit",
                    "改过的描述",
                    "先 build，再改代码"));

            result.Success.Should().BeTrue();
            result.SkillName.Should().Be("build-before-edit");
            writer.LastLevel.Should().Be(Seeing.Agent.Abstractions.Configuration.ConfigLevel.User);
            writer.LastName.Should().Be("build-before-edit");
            writer.LastDescription.Should().Be("改过的描述");
            writer.LastContent.Should().Be("先 build，再改代码");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public async Task CreateSkill_Should_Honour_Explicit_User_Level()
    {
        // 用户显式要求用户级时必须照办（技能页晋升路径会用到）。
        var directory = NewTempDirectory();
        try
        {
            var journal = new ReflectionJournal(directory);
            await journal.AppendAsync(Record("run_j", withSuggestion: true));

            var writer = new RecordingSkillWriter();
            var operations = new ReflectionRunOperations(
                journal,
                new ReflectionRunReader(directory),
                new ReflectionSuggestionsWriter(
                    new FixedOptionsMonitor(new Seeing.Agent.Reflection.Configuration.ReflectionOptions()),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionSuggestionsWriter>.Instance,
                    writer,
                    catalog: null));

            await operations.CreateSkillAsync(
                "run_j", 0, Seeing.Agent.Abstractions.Configuration.ConfigLevel.User);

            writer.LastLevel.Should().Be(Seeing.Agent.Abstractions.Configuration.ConfigLevel.User);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static ReflectionSuggestionsWriter NewWriter()
        => new(
            new FixedOptionsMonitor(new Seeing.Agent.Reflection.Configuration.ReflectionOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ReflectionSuggestionsWriter>.Instance,
            new RecordingSkillWriter(),
            catalog: null);

    private static Seeing.Agent.Reflection.Abstractions.ReflectionRunRecord Record(
        string runId,
        string executionId = "exec-1",
        bool withSuggestion = false,
        bool chineseTarget = false)
        => new(
            SessionId: "ses-1",
            ExecutionId: executionId,
            Kind: ReflectionTriggerKind.Manual,
            At: DateTimeOffset.UtcNow,
            Outcome: ReflectionRunOutcomes.Suggested,
            CreatedSkills: Array.Empty<string>(),
            Suggestions: withSuggestion || chineseTarget
                ?
                [
                    new ReflectionSuggestion(
                        "skill",
                        chineseTarget ? "先编译再改代码" : "build-before-edit",
                        "先 dotnet build，再改代码",
                        "改代码前先跑构建：在编辑项目源码前执行 dotnet build。",
                        "能提前暴露编译错误",
                        new[] { "第 1 轮直接改了代码" })
                ]
                : Array.Empty<ReflectionSuggestion>(),
            InputTokens: 10,
            OutputTokens: 5,
            RunId: runId);

    private static Seeing.Agent.Reflection.Abstractions.ReflectionFailureRecord FailureRecord(string runId)
        => new(
            SessionId: "ses-1",
            ExecutionId: "exec-1",
            Kind: ReflectionTriggerKind.Manual,
            At: DateTimeOffset.UtcNow,
            SkipReason: ReflectionSkipReasons.Timeout,
            ChildSessionId: "child-1",
            Transcript: "partial",
            Detail: "超时",
            RunId: runId);

    /// <summary>固定快照的 monitor（本组测试只关心模式与级别）。</summary>
    private sealed class FixedOptionsMonitor(Seeing.Agent.Reflection.Configuration.ReflectionOptions value)
        : Microsoft.Extensions.Options.IOptionsMonitor<Seeing.Agent.Reflection.Configuration.ReflectionOptions>
    {
        public Seeing.Agent.Reflection.Configuration.ReflectionOptions CurrentValue { get; } = value;

        public Seeing.Agent.Reflection.Configuration.ReflectionOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(
            Action<Seeing.Agent.Reflection.Configuration.ReflectionOptions, string?> listener) => null;
    }

    /// <summary>
    /// 记录写入层级与名称，用于断言「写到了哪一级」。
    /// <para>
    /// 刻意<b>不</b>伪装成成功写盘并返回真实目录：本组测试关心的是调用参数，
    /// 落到真实文件系统只会让断言依赖磁盘状态。
    /// </para>
    /// </summary>
    private sealed class RecordingSkillWriter : Seeing.Agent.Abstractions.Skills.ISkillWriter
    {
        public Seeing.Agent.Abstractions.Configuration.ConfigLevel? LastLevel { get; private set; }

        public string? LastName { get; private set; }

        public string? LastDescription { get; private set; }

        public string? LastContent { get; private set; }

        public Task<Seeing.Agent.Abstractions.Skills.SkillWriteResult> CreateAsync(
            Seeing.Agent.Abstractions.Skills.SkillDraft draft,
            Seeing.Agent.Abstractions.Configuration.ConfigLevel level,
            bool overwrite,
            CancellationToken ct = default)
        {
            LastLevel = level;
            LastName = draft.Name;
            LastDescription = draft.Description;
            LastContent = draft.Content;
            return Task.FromResult(new Seeing.Agent.Abstractions.Skills.SkillWriteResult(true, null, null));
        }

        public Task<Seeing.Agent.Abstractions.Skills.SkillWriteResult> WriteMarkdownAsync(
            string name, string rawContent, Seeing.Agent.Abstractions.Configuration.ConfigLevel level,
            bool overwrite, CancellationToken ct = default)
            => Task.FromResult(new Seeing.Agent.Abstractions.Skills.SkillWriteResult(true, null, null));

        public Task<Seeing.Agent.Abstractions.Skills.SkillWriteResult> UpdateAsync(
            string name, Seeing.Agent.Abstractions.Skills.SkillDraft draft, CancellationToken ct = default)
            => Task.FromResult(new Seeing.Agent.Abstractions.Skills.SkillWriteResult(true, null, null));

        public Task<Seeing.Agent.Abstractions.Skills.SkillWriteResult> DeleteAsync(
            string name, CancellationToken ct = default)
            => Task.FromResult(new Seeing.Agent.Abstractions.Skills.SkillWriteResult(true, null, null));

        public Task<Seeing.Agent.Abstractions.Skills.SkillWriteResult> InstallZipAsync(
            string name, Stream zip, Seeing.Agent.Abstractions.Configuration.ConfigLevel level, bool overwrite,
            string? subdirectoryPrefix, CancellationToken ct = default)
            => Task.FromResult(new Seeing.Agent.Abstractions.Skills.SkillWriteResult(true, null, null));

        public Task<Seeing.Agent.Abstractions.Skills.SkillWriteResult> SetEnabledAsync(
            string name, bool enabled, Seeing.Agent.Abstractions.Configuration.ConfigLevel level,
            CancellationToken ct = default)
            => Task.FromResult(new Seeing.Agent.Abstractions.Skills.SkillWriteResult(true, null, null));
    }
}
