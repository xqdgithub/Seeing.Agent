using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Questions;

public class QuestionInteractionServiceTests
{
    [Fact]
    public void Submit_LongCustomAnswer_ShouldTruncateTo2000AndPassSession()
    {
        var manager = new CapturingQuestionManager();
        var service = new QuestionInteractionService(manager);
        var card = new QuestionCardModel { RequestId = "r1", SessionId = "s1" };
        var answers = new[]
        {
            new QuestionAnswer { QuestionId = "q1", CustomAnswer = new string('x', 2500) }
        };

        service.Submit(card, answers).Should().BeTrue();

        manager.LastResult.Should().NotBeNull();
        manager.LastResult!.Status.Should().Be(QuestionResultStatus.Completed);
        manager.LastResult.Answers.Single().CustomAnswer!.Length.Should().Be(2000);
        manager.LastExpectedSessionId.Should().Be("s1");
    }

    [Fact]
    public void Cancel_ShouldResolveCancelledWithoutAnswers()
    {
        var manager = new CapturingQuestionManager();
        var service = new QuestionInteractionService(manager);
        var card = new QuestionCardModel { RequestId = "r1", SessionId = "s1" };

        service.Cancel(card).Should().BeTrue();

        manager.LastResult!.Status.Should().Be(QuestionResultStatus.Cancelled);
        manager.LastResult.Answers.Should().BeEmpty();
    }

    [Fact]
    public void Submit_ResolvedCard_ShouldNoOp()
    {
        var manager = new CapturingQuestionManager();
        var service = new QuestionInteractionService(manager);
        var card = new QuestionCardModel { RequestId = "r1", SessionId = "s1" };
        card.IsPending = false;

        service.Submit(card, Array.Empty<QuestionAnswer>()).Should().BeFalse();
        manager.LastResult.Should().BeNull();
    }

    private sealed class CapturingQuestionManager : IQuestionRequestManager
    {
        public QuestionResult? LastResult { get; private set; }

        public string? LastExpectedSessionId { get; private set; }

        public event Action? PendingChanged;

        public Task<RequestTicket> BeginAsync(QuestionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<QuestionResult> WaitAsync(RequestTicket ticket, CancellationToken ct = default)
            => throw new NotSupportedException();

        public bool TryResolve(string requestId, QuestionResult response, string? expectedSessionId = null)
        {
            LastResult = response;
            LastExpectedSessionId = expectedSessionId;
            return true;
        }

        public IReadOnlyList<QuestionRequest> GetPending(string sessionId) => Array.Empty<QuestionRequest>();

        public IReadOnlyList<QuestionRequest> GetAllPending() => Array.Empty<QuestionRequest>();

        public int PendingCount => 0;

        public void Dispose()
        {
        }
    }
}
