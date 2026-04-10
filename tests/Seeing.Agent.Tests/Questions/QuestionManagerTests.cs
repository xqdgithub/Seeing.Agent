using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Questions;
using Xunit;

namespace Seeing.Agent.Tests.Questions
{
    /// <summary>
    /// QuestionManager 单元测试
    /// </summary>
    public class QuestionManagerTests
    {
        private readonly Mock<ILogger<QuestionManager>> _loggerMock;
        private readonly QuestionManager _manager;

        public QuestionManagerTests()
        {
            _loggerMock = new Mock<ILogger<QuestionManager>>();
            _manager = new QuestionManager(_loggerMock.Object);
        }

        [Fact]
        public async Task AskAsync_WhenAnswered_ReturnsResult()
        {
            // Arrange
            var request = new QuestionRequest
            {
                Id = "q_test_001",
                SessionId = "session_001",
                Questions = new List<Question>
                {
                    new Question
                    {
                        Id = "q1",
                        Header = "选择选项",
                        QuestionText = "请选择一个选项",
                        Options = new List<QuestionOption>
                        {
                            new QuestionOption { Label = "选项A", Description = "第一个选项" },
                            new QuestionOption { Label = "选项B", Description = "第二个选项" }
                        }
                    }
                }
            };

            var answers = new List<QuestionAnswer>
            {
                new QuestionAnswer
                {
                    QuestionId = "q1",
                    SelectedLabels = new List<string> { "选项A" }
                }
            };

            // Act - 启动提问任务（异步）
            var askTask = _manager.AskAsync(request);

            // 模拟用户回答
            await _manager.AnswerAsync(request.Id, answers);

            var result = await askTask;

            // Assert
            result.Should().NotBeNull();
            result.RequestId.Should().Be(request.Id);
            result.Answers.Should().HaveCount(1);
            result.Answers[0].QuestionId.Should().Be("q1");
            result.Answers[0].SelectedLabels.Should().Contain("选项A");
        }

        [Fact]
        public async Task AskAsync_WhenRejected_ThrowsQuestionRejectedException()
        {
            // Arrange
            var request = new QuestionRequest
            {
                Id = "q_test_002",
                SessionId = "session_002",
                Questions = new List<Question>
                {
                    new Question
                    {
                        Id = "q1",
                        Header = "确认操作",
                        QuestionText = "是否继续执行？"
                    }
                }
            };

            // Act - 启动提问任务（异步）
            var askTask = _manager.AskAsync(request);

            // 模拟用户拒绝
            await _manager.RejectAsync(request.Id);

            // Assert
            var exception = await Assert.ThrowsAsync<QuestionRejectedException>(() => askTask);
            exception.Message.Should().Contain("取消");
        }

        [Fact]
        public async Task GetPendingAsync_ReturnsPendingQuestions()
        {
            // Arrange
            var request1 = new QuestionRequest
            {
                Id = "q_test_003",
                SessionId = "session_003",
                Questions = new List<Question>
                {
                    new Question { Header = "问题1", QuestionText = "这是问题一" }
                }
            };

            var request2 = new QuestionRequest
            {
                Id = "q_test_004",
                SessionId = "session_003",
                Questions = new List<Question>
                {
                    new Question { Header = "问题2", QuestionText = "这是问题二" }
                }
            };

            // Act - 启动两个提问任务（不等待）
            var task1 = _manager.AskAsync(request1);
            var task2 = _manager.AskAsync(request2);

            // 获取待处理问题
            var pending = await _manager.GetPendingAsync();

            // Assert
            pending.Should().HaveCount(2);
            pending.Select(p => p.Id).Should().Contain(new[] { "q_test_003", "q_test_004" });

            // 清理 - 回答问题以完成任务
            await _manager.AnswerAsync(request1.Id, new List<QuestionAnswer>());
            await _manager.AnswerAsync(request2.Id, new List<QuestionAnswer>());
            await task1;
            await task2;
        }

        [Fact]
        public async Task AnswerAsync_ForUnknownRequest_DoesNotThrow()
        {
            // Arrange
            var unknownId = "q_unknown_001";
            var answers = new List<QuestionAnswer>
            {
                new QuestionAnswer { QuestionId = "q1", SelectedLabels = new List<string> { "答案" } }
            };

            // Act
            await _manager.AnswerAsync(unknownId, answers);

            // Assert - 不应抛出异常
            // 验证日志记录了警告（可选）
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("未知请求")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}