using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Core.Tools.Question;
using Xunit;

namespace Seeing.Agent.Tools.Question.Tests;

public class QuestionToolTests
{
    private const string AnswersBegin = "<<<USER_ANSWERS_BEGIN>>>";
    private const string AnswersEnd = "<<<USER_ANSWERS_END>>>";

    private static QuestionTool CreateTool() => new(NullLogger<QuestionTool>.Instance);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ToolContext Context(
        IQuestionRequestManager? manager = null,
        IQuestionSurfaceRegistry? registry = null)
    {
        var context = new ToolContext
        {
            SessionId = "s1",
            MessageId = "m1",
            CallId = "c1"
        };

        if (manager is not null || registry is not null)
        {
            var services = new StubServiceProvider();
            if (manager is not null)
                services.Add(manager);
            if (registry is not null)
                services.Add(registry);
            context.Services = services;
        }

        return context;
    }

    private static Mock<IQuestionRequestManager> ManagerReturning(QuestionResult result)
    {
        var manager = new Mock<IQuestionRequestManager>();
        manager.Setup(m => m.BeginAsync(It.IsAny<QuestionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestTicket("r1", "s1"));
        manager.Setup(m => m.WaitAsync(It.IsAny<RequestTicket>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return manager;
    }

    private static JsonElement ValidArgs() => Args(new
    {
        questions = new object[]
        {
            new
            {
                id = "q1",
                header = "选择",
                question = "请选择一项",
                kind = "single",
                options = new object[]
                {
                    new { label = "A" },
                    new { label = "B" }
                }
            }
        }
    });

    private static QuestionResult Completed(params QuestionAnswer[] answers) => new()
    {
        RequestId = "r1",
        Status = QuestionResultStatus.Completed,
        Answers = answers.ToList()
    };

    [Fact]
    public void ParametersSchema_Should_Define_QuestionsContract()
    {
        var schema = CreateTool().ParametersSchema;

        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("questions");

        var questions = schema.GetProperty("properties").GetProperty("questions");
        questions.GetProperty("type").GetString().Should().Be("array");
        questions.GetProperty("minItems").GetInt32().Should().Be(1);

        var item = questions.GetProperty("items");
        item.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["id", "header", "question"]);

        item.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["single", "multiple", "text"]);
    }

    [Fact]
    public async Task TooManyQuestions_Should_Fail()
    {
        var questions = Enumerable.Range(0, 11)
            .Select(i => new { id = $"q{i}", header = "H", question = "Q" })
            .ToArray();

        var result = await CreateTool().ExecuteAsync(
            Args(new { questions }), Context(ManagerReturning(Completed()).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("超限");
    }

    [Fact]
    public async Task TooManyOptions_Should_Fail()
    {
        var options = Enumerable.Range(0, 21).Select(i => new { label = $"O{i}" }).ToArray();
        var questions = new object[] { new { id = "q1", header = "H", question = "Q", options } };

        var result = await CreateTool().ExecuteAsync(
            Args(new { questions }), Context(ManagerReturning(Completed()).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("选项超限");
    }

    [Fact]
    public async Task HeaderTooLong_Should_Fail()
    {
        var questions = new object[]
        {
            new { id = "q1", header = new string('h', 31), question = "Q" }
        };

        var result = await CreateTool().ExecuteAsync(
            Args(new { questions }), Context(ManagerReturning(Completed()).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("header 超长");
    }

    [Fact]
    public async Task TextQuestion_WithOptions_Should_Fail()
    {
        var questions = new object[]
        {
            new
            {
                id = "q1",
                header = "H",
                question = "Q",
                kind = "text",
                options = new object[] { new { label = "A" } }
            }
        };

        var result = await CreateTool().ExecuteAsync(
            Args(new { questions }), Context(ManagerReturning(Completed()).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("options 必须为空");
    }

    [Fact]
    public async Task DuplicateIds_Should_Fail()
    {
        var questions = new object[]
        {
            new { id = "q1", header = "H", question = "Q" },
            new { id = "q1", header = "H2", question = "Q2" }
        };

        var result = await CreateTool().ExecuteAsync(
            Args(new { questions }), Context(ManagerReturning(Completed()).Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("重复");
    }

    [Fact]
    public async Task MissingManager_Should_Fail()
    {
        var result = await CreateTool().ExecuteAsync(ValidArgs(), new ToolContext { SessionId = "s1" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("问题交互通道未接入");
    }

    [Fact]
    public async Task CannotSurface_Should_Fail_And_NotBegin()
    {
        var manager = ManagerReturning(Completed());
        var registry = new Mock<IQuestionSurfaceRegistry>();
        registry.Setup(r => r.CanSurface("s1")).Returns(false);

        var result = await CreateTool().ExecuteAsync(ValidArgs(), Context(manager.Object, registry.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("不支持交互提问");
        manager.Verify(
            m => m.BeginAsync(It.IsAny<QuestionRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Completed_Should_FormatStructuredAnswers()
    {
        var manager = ManagerReturning(Completed(new QuestionAnswer
        {
            QuestionId = "q1",
            SelectedLabels = ["A"]
        }));

        var result = await CreateTool().ExecuteAsync(ValidArgs(), Context(manager.Object));

        result.Success.Should().BeTrue();
        result.Output.Should().Contain(AnswersBegin).And.Contain(AnswersEnd);
        result.Metadata["status"].Should().Be("completed");

        var payload = ExtractPayload(result.Output);
        var answers = JsonSerializer.Deserialize<List<QuestionAnswer>>(payload);
        answers.Should().ContainSingle().Which.SelectedLabels.Should().Contain("A");
    }

    [Fact]
    public async Task Cancelled_Should_Fail_WithMessage()
    {
        var manager = ManagerReturning(new QuestionResult
        {
            RequestId = "r1",
            Status = QuestionResultStatus.Cancelled
        });

        var result = await CreateTool().ExecuteAsync(ValidArgs(), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("用户取消了作答");
    }

    [Fact]
    public async Task Timeout_Should_Fail_WithMessage()
    {
        var manager = ManagerReturning(new QuestionResult
        {
            RequestId = "r1",
            Status = QuestionResultStatus.Timeout
        });

        var result = await CreateTool().ExecuteAsync(ValidArgs(), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("未在限定时间内作答");
    }

    [Fact]
    public async Task Unavailable_Should_Fail_WithMessage()
    {
        var manager = ManagerReturning(new QuestionResult
        {
            RequestId = "r1",
            Status = QuestionResultStatus.Unavailable
        });

        var result = await CreateTool().ExecuteAsync(ValidArgs(), Context(manager.Object));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("不可用");
    }

    [Fact]
    public async Task Execute_Should_Propagate_CancellationToken()
    {
        CancellationToken beginToken = default;
        CancellationToken waitToken = default;

        var manager = new Mock<IQuestionRequestManager>();
        manager.Setup(m => m.BeginAsync(It.IsAny<QuestionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<QuestionRequest, CancellationToken>((_, token) => beginToken = token)
            .ReturnsAsync(new RequestTicket("r1", "s1"));
        manager.Setup(m => m.WaitAsync(It.IsAny<RequestTicket>(), It.IsAny<CancellationToken>()))
            .Callback<RequestTicket, CancellationToken>((_, token) => waitToken = token)
            .ReturnsAsync(new QuestionResult
            {
                RequestId = "r1",
                Status = QuestionResultStatus.Cancelled
            });

        using var cts = new CancellationTokenSource();
        var context = Context(manager.Object);
        context.CancellationToken = cts.Token;

        var result = await CreateTool().ExecuteAsync(ValidArgs(), context);

        beginToken.Should().Be(cts.Token);
        waitToken.Should().Be(cts.Token);
        result.Success.Should().BeFalse();
    }

    private static string ExtractPayload(string output)
    {
        var start = output.IndexOf(AnswersBegin, StringComparison.Ordinal) + AnswersBegin.Length;
        var end = output.IndexOf(AnswersEnd, StringComparison.Ordinal);
        return output[start..end].Trim();
    }
}

/// <summary>极简 IServiceProvider（测试用，仅按类型注册实例）。</summary>
internal sealed class StubServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _map = new();

    public StubServiceProvider Add<T>(T instance) where T : notnull
    {
        _map[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType) =>
        _map.TryGetValue(serviceType, out var value) ? value : null;
}
