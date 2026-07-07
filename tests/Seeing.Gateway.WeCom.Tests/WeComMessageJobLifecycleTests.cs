using FluentAssertions;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComMessageJobLifecycleTests
{
    [Fact]
    public void JobTimeout_ShouldMatchEffectiveProcessingMaxDuration()
    {
        var options = new WeComOptions { ProcessingMaxDurationSeconds = 0 };

        options.EffectiveProcessingMaxDurationSeconds.Should().Be(WeComOptions.DefaultProcessingMaxDurationSeconds);
    }

    [Fact]
    public void JobTimeout_ShouldUseConfiguredValue()
    {
        var options = new WeComOptions { ProcessingMaxDurationSeconds = 1800 };

        options.EffectiveProcessingMaxDurationSeconds.Should().Be(1800);
    }
}
