using FluentAssertions;
using Seeing.Gateway.WeCom.Connection;
using Xunit;

namespace Seeing.Gateway.WeCom.Tests;

public class WeComConnectionStateTests
{
    [Fact]
    public void WeComConnectionEpochException_ShouldIncludeEpochs()
    {
        var ex = new WeComConnectionEpochException(3, 5);

        ex.ExpectedEpoch.Should().Be(3);
        ex.CurrentEpoch.Should().Be(5);
        ex.Message.Should().Contain("3");
        ex.Message.Should().Contain("5");
    }
}
