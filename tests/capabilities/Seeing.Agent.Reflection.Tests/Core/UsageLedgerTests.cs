using System.Text;
using FluentAssertions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Reflection.Core;
using Xunit;

namespace Seeing.Agent.Reflection.Tests.Core;

/// <summary>用量账本的累加、跨天重置、损坏容错与跨实例持久化。</summary>
public sealed class UsageLedgerTests
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

    private static TokenUsage Usage(int input, int output)
        => new() { InputTokens = input, OutputTokens = output };

    [Fact]
    public void Ledger_Should_Accumulate_Not_Overwrite()
    {
        var directory = CreateTempDirectory();
        try
        {
            var ledger = new ExecutionUsageLedger(directory);

            ledger.Add(Usage(100, 50));
            ledger.Add(Usage(200, 80));

            ledger.UsedToday().Should().Be(430);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Ledger_Should_Reset_On_New_Day()
    {
        var directory = CreateTempDirectory();
        try
        {
            // 手工塞一个过期日期，模拟跨天（目录需先创建，否则写文件会失败）。
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "usage.json"),
                """{"date":"2000-01-01","input":9000,"output":9000}""",
                Encoding.UTF8);

            var ledger = new ExecutionUsageLedger(directory);
            ledger.UsedToday().Should().Be(0);

            ledger.Add(Usage(10, 5));

            ledger.UsedToday().Should().Be(15);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Ledger_Should_Return_Zero_On_Corrupt_File()
    {
        var directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "usage.json"), "not json", Encoding.UTF8);

            var ledger = new ExecutionUsageLedger(directory);

            ledger.UsedToday().Should().Be(0);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Ledger_Should_Persist_Across_Instances()
    {
        var directory = CreateTempDirectory();
        try
        {
            var first = new ExecutionUsageLedger(directory);
            first.Add(Usage(70, 30));
            first.UsedToday().Should().Be(100);

            var second = new ExecutionUsageLedger(directory);

            second.UsedToday().Should().Be(100);
        }
        finally
        {
            TryDelete(directory);
        }
    }
}
