using FluentAssertions;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Seeing.Agent.Files.Tests;

public class RevisionCalculatorTests
{
    [Fact]
    public void FromBytes_ShouldReturnStableLowercaseHash()
    {
        var bytes = Encoding.UTF8.GetBytes("hello");
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var rev = RevisionCalculator.FromBytes(bytes, DateTime.UnixEpoch);

        rev.Hash.Should().Be(expected);
        rev.Size.Should().Be(bytes.Length);
        rev.LastWriteUtc.Should().Be(DateTime.UnixEpoch);
    }

    [Fact]
    public void FromBytes_SameContent_ShouldMatch()
    {
        var a = RevisionCalculator.FromBytes(Encoding.UTF8.GetBytes("x"), DateTime.UnixEpoch);
        var b = RevisionCalculator.FromBytes(Encoding.UTF8.GetBytes("x"), DateTime.UtcNow);

        a.Matches(b).Should().BeTrue();
    }

    [Fact]
    public void FromBytes_DifferentContent_ShouldNotMatch()
    {
        var a = RevisionCalculator.FromBytes(Encoding.UTF8.GetBytes("x"), DateTime.UnixEpoch);
        var b = RevisionCalculator.FromBytes(Encoding.UTF8.GetBytes("y"), DateTime.UnixEpoch);

        a.Matches(b).Should().BeFalse();
    }
}
