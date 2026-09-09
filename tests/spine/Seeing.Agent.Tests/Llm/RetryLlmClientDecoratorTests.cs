using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Core.Llm;
using System.Net;
using System.Runtime.CompilerServices;
using Xunit;

namespace Seeing.Agent.Tests.Llm;

public class RetryLlmClientDecoratorTests
{
    private sealed class FlakyClient : ILlmClient
    {
        private readonly Queue<Exception?> _failures;
        private readonly bool _yieldBeforeFail;

        public int CompleteCalls { get; private set; }
        public int StreamCalls { get; private set; }

        public FlakyClient(IEnumerable<Exception?> failures, bool yieldBeforeFail = false)
        {
            _failures = new Queue<Exception?>(failures);
            _yieldBeforeFail = yieldBeforeFail;
        }

        public string ProviderId => "p";
        public string ProviderType => ProviderTypes.OpenAi;

        public Task<ChatResponse> CompleteAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            var next = _failures.Dequeue();
            if (next is not null)
                throw next;
            return Task.FromResult(new ChatResponse
            {
                Id = "r",
                Model = request.Model,
                Message = new ChatMessage { Role = "assistant", Content = "ok" },
                FinishReason = "stop"
            });
        }

        public async IAsyncEnumerable<StreamUpdate> CompleteStreamAsync(
            ChatRequest request,
            LlmCallContext? call = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            var next = _failures.Dequeue();
            if (_yieldBeforeFail)
                yield return new StreamUpdate { ContentDelta = "partial" };

            if (next is not null)
                throw next;

            yield return new StreamUpdate { ContentDelta = "ok", IsComplete = true };
            await Task.CompletedTask;
        }

        public Task<bool> TestConnectionAsync(
            string modelId,
            LlmCallContext? call = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private static ILlmClient Wrap(ILlmClient inner, int maxRetries = 3)
    {
        var decorator = new RetryLlmClientDecorator(NullLoggerFactory.Instance);
        return decorator.Wrap(inner, new ProviderConfig
        {
            Id = "p",
            Type = ProviderTypes.OpenAi,
            MaxRetries = maxRetries
        });
    }

    [Fact]
    public async Task CompleteAsync_RetriesTransientThenSucceeds_WritesItems()
    {
        var inner = new FlakyClient(
        [
            new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable),
            null
        ]);
        var client = Wrap(inner, maxRetries: 3);
        var call = new LlmCallContext();

        var response = await client.CompleteAsync(new ChatRequest { Model = "m" }, call);

        response.Message.Content.Should().Be("ok");
        inner.CompleteCalls.Should().Be(2);
        call.Items[LlmRetryPolicy.AttemptItemKey].Should().Be(2);
        call.Items[LlmRetryPolicy.WillRetryItemKey].Should().Be(false);
        call.Items[LlmRetryPolicy.MaxRetriesItemKey].Should().Be(3);
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRetryHttp4xx()
    {
        var inner = new FlakyClient([new HttpRequestException("400", null, HttpStatusCode.BadRequest)]);
        var client = Wrap(inner, maxRetries: 3);

        var act = () => client.CompleteAsync(new ChatRequest { Model = "m" });
        await act.Should().ThrowAsync<HttpRequestException>();
        inner.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public async Task CompleteStreamAsync_RetriesWhenNoChunkYielded()
    {
        var inner = new FlakyClient(
        [
            new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable),
            null
        ]);
        var client = Wrap(inner, maxRetries: 3);

        var chunks = new List<StreamUpdate>();
        await foreach (var u in client.CompleteStreamAsync(new ChatRequest { Model = "m" }))
            chunks.Add(u);

        chunks.Should().ContainSingle(c => c.ContentDelta == "ok");
        inner.StreamCalls.Should().Be(2);
    }

    [Fact]
    public async Task CompleteStreamAsync_DoesNotRetryAfterYield()
    {
        var inner = new FlakyClient(
            [new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable)],
            yieldBeforeFail: true);
        var client = Wrap(inner, maxRetries: 3);

        var act = async () =>
        {
            await foreach (var _ in client.CompleteStreamAsync(new ChatRequest { Model = "m" }))
            {
            }
        };

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.StreamCalls.Should().Be(1);
    }
}
