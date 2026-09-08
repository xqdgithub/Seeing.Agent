using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

public class DispatchTargetCleanupTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Deserialize_OldJobsJson_IgnoresChannelAndUserId()
    {
        var json = """
            {"version":2,"jobs":[{"id":"j1","dispatch":{"target":{"sessionId":"main","channel":"qq","userId":"u1"}}}]}
            """;
        var file = JsonSerializer.Deserialize<JobsFile>(json, JsonOptions);
        file!.Jobs[0].Dispatch.Target.SessionId.Should().Be("main");
        var roundtrip = JsonSerializer.Serialize(file, JsonOptions);
        roundtrip.Should().NotContain("channel");
        roundtrip.Should().NotContain("userId");
    }
}
