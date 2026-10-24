using System.Text.Json.Nodes;
using FluentAssertions;
using Seeing.Agent.Core.Context;
using Seeing.Session.Core;
using Xunit;

namespace Seeing.Agent.Tests.Context;

public sealed class ContextBaselineStoreTests
{
    [Fact]
    public void SaveThenLoad_ShouldRoundTrip()
    {
        var s = new SessionData { Id = "s1" };
        var state = new ContextBaselineState();
        state.Pending = PendingBaselineReset.Compaction;
        state.Sources["k"] = new ContextSourceState { ValueSchemaVersion = 3, BaselineText = "X" };
        ContextBaselineStore.Save(s, state);

        var loaded = ContextBaselineStore.Load(s);
        loaded.Pending.Should().HaveFlag(PendingBaselineReset.Compaction);
        loaded.Sources["k"].ValueSchemaVersion.Should().Be(3);
        loaded.Sources["k"].BaselineText.Should().Be("X");
    }

    [Fact]
    public void Load_WithUnknownStateVersion_ShouldReturnEmpty()
    {
        var s = new SessionData { Id = "s1" };
        s.Metadata[SessionMetadataKeys.ContextBaseline] = """{"stateVersion":999,"sources":{}}""";

        ContextBaselineStore.Load(s).Sources.Should().BeEmpty();
    }

    [Fact]
    public void Load_WithCorruptJson_ShouldReturnEmptyAndNotThrow()
    {
        var s = new SessionData { Id = "s1" };
        s.Metadata[SessionMetadataKeys.ContextBaseline] = "{not json";

        ContextBaselineStore.Load(s).Sources.Should().BeEmpty();
    }

    [Fact]
    public void SaveThenLoad_ShouldRoundTripCombinedFlagsAndJsonValues()
    {
        var s = new SessionData { Id = "s1" };
        var state = new ContextBaselineState
        {
            Pending = PendingBaselineReset.Compaction | PendingBaselineReset.WorkspaceChanged
        };
        state.Sources["k"] = new ContextSourceState
        {
            ValueSchemaVersion = 2,
            BaselineText = "T",
            BaselineHash = "H",
            BaselineValues = JsonNode.Parse("""{"b":1,"a":{"c":true}}""")!.AsObject(),
            CurrentValues = JsonNode.Parse("""{"a":2}""")!.AsObject(),
            BaselineMessageId = "msg_1",
            DeltaLedger = { new DeltaLedgerEntry("msg_2", JsonNode.Parse("""{"x":"y"}""")!.AsObject()) }
        };

        ContextBaselineStore.Save(s, state);
        var loaded = ContextBaselineStore.Load(s);

        loaded.Pending.Should().Be(PendingBaselineReset.Compaction | PendingBaselineReset.WorkspaceChanged);
        loaded.Sources["k"].BaselineValues!["b"]!.GetValue<int>().Should().Be(1);
        loaded.Sources["k"].BaselineValues!["a"]!["c"]!.GetValue<bool>().Should().BeTrue();
        loaded.Sources["k"].CurrentValues!["a"]!.GetValue<int>().Should().Be(2);
        loaded.Sources["k"].BaselineMessageId.Should().Be("msg_1");
        loaded.Sources["k"].DeltaLedger.Should().ContainSingle();
        loaded.Sources["k"].DeltaLedger[0].MessageId.Should().Be("msg_2");
        loaded.Sources["k"].DeltaLedger[0].Values["x"]!.GetValue<string>().Should().Be("y");
    }

    [Fact]
    public void Clear_ShouldRemoveMetadataKey()
    {
        var s = new SessionData { Id = "s1" };
        s.Metadata[SessionMetadataKeys.ContextBaseline] = "{}";

        ContextBaselineStore.Clear(s);

        s.Metadata.Should().NotContainKey(SessionMetadataKeys.ContextBaseline);
    }
}
