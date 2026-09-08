using FluentAssertions;
using Seeing.Agent.Core.Detection;
using Xunit;

namespace Seeing.Agent.Tests.Detection
{
    /// <summary>
    /// LoopDetector 单元测试
    /// </summary>
    public class LoopDetectorTests
    {
        [Fact]
        public void Constructor_ShouldSetDefaultThreshold_WhenNotSpecified()
        {
            var detector = new LoopDetector();

            // 默认阈值为 3
            detector.Check("tool1", "hash1").ConsecutiveCount.Should().Be(1);
        }

        [Fact]
        public void Constructor_ShouldThrow_WhenThresholdLessThanTwo()
        {
            var act = () => new LoopDetector(1);

            act.Should().Throw<ArgumentException>()
                .WithMessage("阈值必须至少为 2*");
        }

        [Fact]
        public void Check_ShouldDetectLoop_WhenSameToolCalledConsecutively()
        {
            var detector = new LoopDetector(threshold: 3);
            const string toolName = "read_file";
            const string argsHash = "abc123";

            // 第一次调用
            var result1 = detector.Check(toolName, argsHash);
            result1.IsLoop.Should().BeFalse();
            result1.ConsecutiveCount.Should().Be(1);
            result1.RecommendedAction.Should().Be(LoopAction.Continue);

            // 第二次调用（相同）
            var result2 = detector.Check(toolName, argsHash);
            result2.IsLoop.Should().BeFalse();
            result2.ConsecutiveCount.Should().Be(2);
            result2.RecommendedAction.Should().Be(LoopAction.Continue);

            // 第三次调用（相同）- 触发循环检测
            var result3 = detector.Check(toolName, argsHash);
            result3.IsLoop.Should().BeTrue();
            result3.ConsecutiveCount.Should().Be(3);
            result3.RecommendedAction.Should().Be(LoopAction.Warn);
            result3.ToolName.Should().Be(toolName);
        }

        [Fact]
        public void Check_ShouldResetCount_WhenDifferentToolCalled()
        {
            var detector = new LoopDetector(threshold: 3);

            // 连续调用相同工具
            detector.Check("tool1", "hash1");
            detector.Check("tool1", "hash1");

            // 调用不同工具
            var result = detector.Check("tool2", "hash2");

            result.IsLoop.Should().BeFalse();
            result.ConsecutiveCount.Should().Be(1);
            result.ToolName.Should().Be("tool2");
        }

        [Fact]
        public void Check_ShouldResetCount_WhenSameToolButDifferentArgs()
        {
            var detector = new LoopDetector(threshold: 3);

            // 调用相同工具，不同参数
            detector.Check("tool1", "hash1");
            detector.Check("tool1", "hash1");

            var result = detector.Check("tool1", "hash2"); // 不同参数哈希

            result.IsLoop.Should().BeFalse();
            result.ConsecutiveCount.Should().Be(1);
        }

        [Fact]
        public void Reset_ShouldClearAllState()
        {
            var detector = new LoopDetector(threshold: 3);

            // 建立一些状态
            detector.Check("tool1", "hash1");
            detector.Check("tool1", "hash1");
            detector.Check("tool1", "hash1");

            // 重置
            detector.Reset();

            // 验证状态已清空
            var result = detector.Check("tool2", "hash2");
            result.ConsecutiveCount.Should().Be(1);
            result.IsLoop.Should().BeFalse();
        }

        [Fact]
        public void RecordCall_ShouldTrackCallHistory()
        {
            var detector = new LoopDetector(threshold: 3);

            detector.RecordCall("tool1", "hash1");
            detector.RecordCall("tool2", "hash2");

            var history = detector.GetCallHistory();
            history.Should().HaveCount(2);
            history[0].ToolName.Should().Be("tool1");
            history[1].ToolName.Should().Be("tool2");
        }

        [Fact]
        public void ComputeArgumentsHash_ShouldReturnConsistentHash()
        {
            var args = "{\"path\": \"/test/file.txt\"}";

            var hash1 = LoopDetector.ComputeArgumentsHash(args);
            var hash2 = LoopDetector.ComputeArgumentsHash(args);

            hash1.Should().Be(hash2);
            hash1.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void ComputeArgumentsHash_ShouldReturnDifferentHashForDifferentArgs()
        {
            var args1 = "{\"path\": \"/test/file1.txt\"}";
            var args2 = "{\"path\": \"/test/file2.txt\"}";

            var hash1 = LoopDetector.ComputeArgumentsHash(args1);
            var hash2 = LoopDetector.ComputeArgumentsHash(args2);

            hash1.Should().NotBe(hash2);
        }

        [Fact]
        public void RecommendedAction_ShouldBeTerminate_WhenCountExceedsThresholdByTwo()
        {
            var detector = new LoopDetector(threshold: 3);

            detector.Check("tool1", "hash1");
            detector.Check("tool1", "hash1");
            detector.Check("tool1", "hash1"); // threshold reached -> Warn
            var result = detector.Check("tool1", "hash1"); // threshold + 1 -> Warn
            var result2 = detector.Check("tool1", "hash1"); // threshold + 2 -> Terminate

            result.RecommendedAction.Should().Be(LoopAction.Warn);
            result2.RecommendedAction.Should().Be(LoopAction.Terminate);
        }

        [Fact]
        public void Check_ShouldBeThreadSafe()
        {
            var detector = new LoopDetector(threshold: 3);
            const int threadCount = 10;
            const int iterationsPerThread = 100;
            var exceptions = new List<Exception>();
            var results = new List<LoopDetectionResult>();

            Parallel.For(0, threadCount, i =>
            {
                try
                {
                    for (int j = 0; j < iterationsPerThread; j++)
                    {
                        var result = detector.Check("tool1", "hash1");
                        lock (results)
                        {
                            results.Add(result);
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            exceptions.Should().BeEmpty("并发操作不应抛出异常");
            results.Should().HaveCount(threadCount * iterationsPerThread);
        }

        [Fact]
        public void Check_ShouldThrow_WhenToolNameIsNull()
        {
            var detector = new LoopDetector();

            var act = () => detector.Check(null!, "hash1");

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Check_ShouldThrow_WhenArgumentsHashIsNull()
        {
            var detector = new LoopDetector();

            var act = () => detector.Check("tool1", null!);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void RecordCall_ShouldThrow_WhenToolNameIsNull()
        {
            var detector = new LoopDetector();

            var act = () => detector.RecordCall(null!, "hash1");

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void GetCallHistory_ShouldLimitSize_WhenExceedsMaxSize()
        {
            var detector = new LoopDetector(threshold: 3);

            // 记录超过 100 次调用
            for (int i = 0; i < 150; i++)
            {
                detector.RecordCall($"tool{i}", $"hash{i}");
            }

            var history = detector.GetCallHistory();
            history.Should().HaveCount(100); // MaxHistorySize = 100
        }
    }
}