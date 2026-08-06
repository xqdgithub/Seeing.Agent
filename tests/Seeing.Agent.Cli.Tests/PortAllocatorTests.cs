using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Seeing.Agent.Cli.Services;
using Xunit;

namespace Seeing.Agent.Cli.Tests;

public class PortAllocatorTests
{
    [Fact]
    public void NextAvailable_WhenPreferredFree_ShouldReturnPreferred()
    {
        var allocator = new PortAllocator();
        var port = allocator.NextAvailable(25000);
        port.Should().Be(25000);
    }

    [Fact]
    public void NextAvailable_WhenPreferredInUse_ShouldReturnLaterFreePort()
    {
        var listener = new TcpListener(IPAddress.Any, 25100);
        listener.Start();
        try
        {
            var allocator = new PortAllocator();
            var port = allocator.NextAvailable(25100);
            port.Should().BeGreaterThan(25100);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void NextAvailable_WhenPreferredInUseOnLoopback_ShouldReturnLaterFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 25200);
        listener.Start();
        try
        {
            var allocator = new PortAllocator();
            var port = allocator.NextAvailable(25200);
            port.Should().BeGreaterThan(25200);
        }
        finally
        {
            listener.Stop();
        }
    }
}