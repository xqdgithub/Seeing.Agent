using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Seeing.Agent.Configuration;
using Seeing.Agent.Llm;
using Seeing.Agent.TokenBudget;
using Seeing.Session.Core;
using Seeing.TokenEstimation;
using Xunit;

namespace Seeing.Agent.Tests.TokenBudget;

public class TokenBudgetManagerTests
{
    [Fact]
    public async Task CalculateBreakdown_WhenMessageIsAddedDuringEstimation_ShouldNotThrow()
    {
        var firstEstimateStarted = new ManualResetEventSlim();
        var allowEstimation = new ManualResetEventSlim();
        var tokenCounter = new Mock<ITokenCounter>();
        tokenCounter
            .Setup(counter => counter.Estimate(It.IsAny<string>()))
            .Callback(() =>
            {
                firstEstimateStarted.Set();
                allowEstimation.Wait();
            })
            .Returns((string content) => content.Length);

        var manager = new TokenBudgetManager(
            Mock.Of<ILlmService>(),
            Options.Create(new SeeingAgentOptions()),
            tokenCounter.Object);
        var session = SessionData.Create();
        session.Messages.Add(SessionMessage.UserMessage("first"));

        try
        {
            var calculation = Task.Run(() => manager.CalculateBreakdown(session));
            firstEstimateStarted.Wait();

            // Simulate ChatEventTracker appending a message while budget calculation is running.
            session.Messages.Add(SessionMessage.AssistantMessage("added concurrently"));
            allowEstimation.Set();

            var act = () => calculation.GetAwaiter().GetResult();
            act.Should().NotThrow();
        }
        finally
        {
            allowEstimation.Set();
            firstEstimateStarted.Dispose();
            allowEstimation.Dispose();
        }
    }
}
