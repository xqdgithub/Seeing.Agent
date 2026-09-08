using System.Text.Json;
using FluentAssertions;
using Seeing.Gateway.QQ.Cards;
using Xunit;

namespace Seeing.Gateway.QQ.Tests;

public class QQCardDispatcherTests
{
    [Fact]
    public async Task TryHandleInteraction_ShouldRouteByPrefix()
    {
        var handled = false;
        var fake = new FakeKind("seeing_perm:", () => { handled = true; return Task.FromResult(true); });
        var dispatcher = new QQCardDispatcher([fake]);

        var json = """
        {
          "data": { "resolved": { "button_data": "seeing_perm:allow:req1" } }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var ok = await dispatcher.TryHandleInteractionAsync(doc.RootElement, CancellationToken.None);
        ok.Should().BeTrue();
        handled.Should().BeTrue();
    }

    [Fact]
    public async Task TryHandleInteraction_UnknownPrefix_ShouldReturnFalse()
    {
        var fake = new FakeKind("seeing_perm:", () => Task.FromResult(true));
        var dispatcher = new QQCardDispatcher([fake]);
        using var doc = JsonDocument.Parse("""{ "button_data": "other:x" }""");
        (await dispatcher.TryHandleInteractionAsync(doc.RootElement, CancellationToken.None)).Should().BeFalse();
    }

    private sealed class FakeKind : IQQCardKind
    {
        private readonly Func<Task<bool>> _handle;
        public FakeKind(string prefix, Func<Task<bool>> handle)
        {
            ActionDataPrefix = prefix;
            _handle = handle;
        }

        public string Name => "fake";
        public string ActionDataPrefix { get; }
        public Task<bool> TryHandleInteractionAsync(JsonElement d, CancellationToken cancellationToken) => _handle();
    }
}
