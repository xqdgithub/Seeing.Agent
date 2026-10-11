using FluentAssertions;
using Seeing.Agent.Hosting.Execution;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Execution;

public class ExecutionRunnerTests
{
    [Fact]
    public void ExecutionRunner_ShouldBeConstructible()
    {
        // 占位契约测试：确认类型存在且 public/internal 可见
        typeof(ExecutionRunner).Should().NotBeNull();
    }
}
