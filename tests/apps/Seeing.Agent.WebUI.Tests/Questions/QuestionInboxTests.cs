using FluentAssertions;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.WebUI.Services;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Questions;

public class QuestionInboxTests
{
    private const string Session = "s1";

    private static QuestionRequest Pending(
        string requestId,
        string sessionId = Session,
        string callId = "call-1",
        params Question[] questions)
        => new()
        {
            Id = requestId,
            SessionId = sessionId,
            Tool = new ToolReference { MessageId = "m1", CallId = callId },
            Questions = questions.Length == 0
                ? new List<Question>
                {
                    new()
                    {
                        Id = "q1",
                        Header = "选择语言",
                        QuestionText = "你偏好哪种语言？",
                        Kind = QuestionKind.Single,
                        Required = true,
                        Options = new List<QuestionOption>
                        {
                            new() { Label = "C#", Description = "强类型" },
                            new() { Label = "F#" }
                        },
                        DefaultSelectedLabels = new List<string> { "C#" }
                    }
                }
                : questions.ToList()
        };

    [Fact]
    public void GetAll_ShouldProjectPendingRequests()
    {
        var manager = new FakeQuestionRequestManager();
        manager.Seed(Pending("r1"), Pending("r2", callId: "call-2"));

        var inbox = new QuestionInbox(manager);

        inbox.GetAll().Select(c => c.RequestId).Should().Equal("r1", "r2");
        inbox.TotalCount.Should().Be(2);
        inbox.GetAll().Should().OnlyContain(c => c.IsPending);
    }

    [Fact]
    public void GetAll_ShouldProjectQuestionsWithOptionsAndDefaults()
    {
        var manager = new FakeQuestionRequestManager();
        manager.Seed(Pending("r1"));

        var inbox = new QuestionInbox(manager);

        var card = inbox.GetAll().Single();
        card.SessionId.Should().Be(Session);
        card.CallId.Should().Be("call-1");
        card.MessageId.Should().Be("m1");

        var question = card.Questions.Single();
        question.Id.Should().Be("q1");
        question.Kind.Should().Be(QuestionKind.Single);
        question.Required.Should().BeTrue();
        question.AllowCustom.Should().BeTrue();
        question.Options.Select(o => o.Label).Should().Equal("C#", "F#");
        question.DefaultSelectedLabels.Should().Equal("C#");
    }

    [Fact]
    public void GetAll_ShouldIgnoreRequestsWithoutId()
    {
        var manager = new FakeQuestionRequestManager();
        manager.Seed(Pending(""), Pending("r1"));

        var inbox = new QuestionInbox(manager);

        inbox.GetAll().Select(c => c.RequestId).Should().Equal("r1");
    }

    [Fact]
    public void GetByCallId_ShouldFilterByCallId()
    {
        var manager = new FakeQuestionRequestManager();
        manager.Seed(Pending("r1", callId: "call-x"), Pending("r2", callId: "call-y"));

        var inbox = new QuestionInbox(manager);

        inbox.GetByCallId("call-x").Should().ContainSingle(c => c.RequestId == "r1");
        inbox.GetByCallId(null).Should().BeEmpty();
    }

    [Fact]
    public void GetBySession_ShouldFilterBySession()
    {
        var manager = new FakeQuestionRequestManager();
        manager.Seed(Pending("r1", sessionId: "s1"), Pending("r2", sessionId: "s2"));

        var inbox = new QuestionInbox(manager);

        inbox.GetBySession("s1").Should().ContainSingle(c => c.RequestId == "r1");
        inbox.GetBySession(null).Should().BeEmpty();
    }

    [Fact]
    public void PendingChanged_ShouldRebuildProjection()
    {
        var manager = new FakeQuestionRequestManager();
        var inbox = new QuestionInbox(manager);
        inbox.TotalCount.Should().Be(0);

        manager.Seed(Pending("r1"));

        inbox.TotalCount.Should().Be(1);
        inbox.GetAll().Single().RequestId.Should().Be("r1");
    }

    [Fact]
    public void PendingChanged_ShouldFireChanged()
    {
        var manager = new FakeQuestionRequestManager();
        var inbox = new QuestionInbox(manager);
        var fired = 0;
        inbox.Changed += () => fired++;

        manager.Seed(Pending("r1"));
        manager.Remove("r1");

        fired.Should().Be(2);
        inbox.TotalCount.Should().Be(0);
    }

    [Fact]
    public void PendingChanged_WithSameRequestIdSequence_ShouldNotFireChanged()
    {
        var manager = new FakeQuestionRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new QuestionInbox(manager);
        var fired = 0;
        inbox.Changed += () => fired++;

        manager.Notify();

        fired.Should().Be(0);
    }

    [Fact]
    public void Dispose_ShouldUnsubscribeAndClear()
    {
        var manager = new FakeQuestionRequestManager();
        manager.Seed(Pending("r1"));
        var inbox = new QuestionInbox(manager);
        var fired = 0;
        inbox.Changed += () => fired++;

        inbox.Dispose();
        manager.Seed(Pending("r2"));

        fired.Should().Be(0);
        inbox.TotalCount.Should().Be(0);
    }

    /// <summary>测试替身：在途问答管理器（GetAllPending 可对账；Seed/Remove/Notify 触发 PendingChanged）。</summary>
    private sealed class FakeQuestionRequestManager : IQuestionRequestManager
    {
        private readonly Dictionary<string, QuestionRequest> _pending = new(StringComparer.Ordinal);

        public event Action? PendingChanged;

        public void Seed(params QuestionRequest[] requests)
        {
            foreach (var request in requests)
            {
                if (!string.IsNullOrEmpty(request.Id))
                    _pending[request.Id] = request;
            }
            PendingChanged?.Invoke();
        }

        public bool Remove(string requestId)
        {
            var removed = _pending.Remove(requestId);
            if (removed)
                PendingChanged?.Invoke();
            return removed;
        }

        public void Notify() => PendingChanged?.Invoke();

        public Task<RequestTicket> BeginAsync(QuestionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<QuestionResult> WaitAsync(RequestTicket ticket, CancellationToken ct = default)
            => throw new NotSupportedException();

        public bool TryResolve(string requestId, QuestionResult response, string? expectedSessionId = null)
        {
            if (_pending.Remove(requestId))
                PendingChanged?.Invoke();
            return true;
        }

        public IReadOnlyList<QuestionRequest> GetPending(string sessionId)
            => string.IsNullOrEmpty(sessionId)
                ? Array.Empty<QuestionRequest>()
                : _pending.Values
                    .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                    .ToList();

        public IReadOnlyList<QuestionRequest> GetAllPending() => _pending.Values.ToList();

        public int PendingCount => _pending.Count;

        public void Dispose()
        {
        }
    }
}
