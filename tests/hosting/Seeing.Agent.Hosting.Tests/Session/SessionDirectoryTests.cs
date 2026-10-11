using FluentAssertions;
using Seeing.Agent.Hosting;
using Seeing.Agent.Abstractions.Runtime;
using Xunit;

namespace Seeing.Agent.Hosting.Tests.Session;

public class SessionDirectoryTests
{
    [Fact]
    public void SessionDirectory_ShouldImplement_ISessionDirectory()
    {
        typeof(ISessionDirectory).IsAssignableFrom(typeof(SessionDirectory)).Should().BeTrue();
    }
}
