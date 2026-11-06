using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Core.Context;
using Xunit;

namespace Seeing.Agent.Tests.Context;

public class ContextExecutionScopeTests
{
    [Fact]
    public void 工具结算视图_应可读写()
    {
        var scope = new ContextExecutionScope();
        scope.ToolSettlement.Should().BeNull();

        scope.ToolSettlement = SessionToolSchemaResolution.Empty;

        scope.ToolSettlement.Should().NotBeNull();
    }
}
