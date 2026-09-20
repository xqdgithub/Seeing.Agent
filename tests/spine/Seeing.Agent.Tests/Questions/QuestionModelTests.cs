using System.Text.Json;
using FluentAssertions;
using Seeing.Agent.Abstractions.Questions;
using Xunit;

namespace Seeing.Agent.Tests.Questions;

public class QuestionModelTests
{
    [Fact]
    public void Question_ShouldSerializeToSpecifiedFieldNames()
    {
        var question = new Question
        {
            Id = "q1",
            Header = "标题",
            QuestionText = "请选择",
            Kind = QuestionKind.Multiple,
            AllowCustom = false,
            Required = false,
            DefaultSelectedLabels = new List<string> { "A" },
            DefaultCustomAnswer = "自定义"
        };

        var json = JsonSerializer.Serialize(question);

        json.Should().Contain("\"question\":");
        json.Should().Contain("\"custom\":");
        json.Should().Contain("\"defaultSelectedLabels\":");
        json.Should().Contain("\"defaultCustomAnswer\":");
        json.Should().Contain("\"kind\":");
        json.Should().NotContain("\"questionText\"");
        json.Should().NotContain("\"allowCustom\"");
    }

    [Fact]
    public void QuestionRequest_ShouldRoundTrip()
    {
        var request = new QuestionRequest
        {
            Id = "req-1",
            SessionId = "s1",
            Tool = new ToolReference { MessageId = "m1", CallId = "c1" },
            Questions = new List<Question>
            {
                new()
                {
                    Id = "q1",
                    Header = "选择",
                    QuestionText = "请选择",
                    Kind = QuestionKind.Multiple,
                    AllowCustom = true,
                    Required = true,
                    DefaultSelectedLabels = new List<string> { "A", "B" },
                    DefaultCustomAnswer = "其他",
                    Options = new List<QuestionOption>
                    {
                        new() { Label = "A", Description = "选项 A" },
                        new() { Label = "B" }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(request);
        var restored = JsonSerializer.Deserialize<QuestionRequest>(json);

        restored.Should().NotBeNull();
        restored.Should().BeEquivalentTo(request);
    }

    [Fact]
    public void QuestionResult_ShouldRoundTripIncludingStatus()
    {
        var result = new QuestionResult
        {
            RequestId = "req-1",
            Status = QuestionResultStatus.Timeout,
            Answers = new List<QuestionAnswer>
            {
                new()
                {
                    QuestionId = "q1",
                    SelectedLabels = new List<string> { "A" },
                    CustomAnswer = "补充"
                }
            }
        };

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<QuestionResult>(json);

        restored.Should().NotBeNull();
        restored.Should().BeEquivalentTo(result);
        restored!.Status.Should().Be(QuestionResultStatus.Timeout);
    }

    [Theory]
    [InlineData(QuestionResultStatus.Completed)]
    [InlineData(QuestionResultStatus.Cancelled)]
    [InlineData(QuestionResultStatus.Timeout)]
    [InlineData(QuestionResultStatus.Unavailable)]
    public void QuestionResultStatus_ShouldRoundTrip(QuestionResultStatus status)
    {
        var json = JsonSerializer.Serialize(new QuestionResult { RequestId = "r", Status = status });
        var restored = JsonSerializer.Deserialize<QuestionResult>(json);

        restored!.Status.Should().Be(status);
    }
}
