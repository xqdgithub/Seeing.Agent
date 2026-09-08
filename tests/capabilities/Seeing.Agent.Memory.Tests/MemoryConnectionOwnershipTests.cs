using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Memory.Core;
using Seeing.Agent.Memory.Integration.Hosting;
using Xunit;

namespace Seeing.Agent.Memory.Tests;

public sealed class MemoryConnectionOwnershipTests
{
    [Fact]
    public async Task MemoryModule_Activate_ShouldOpen_Deactivate_ShouldClose_ReActivate_ShouldReopen()
    {
        var activity = new MemoryModuleActivity();
        var owner = new SqliteConnectionOwner(() => "Data Source=:memory:");
        var module = new MemoryModule(activity, owner);

        owner.IsOpen.Should().BeFalse();

        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider());
        activity.IsActive.Should().BeTrue();
        owner.IsOpen.Should().BeTrue();
        var connection = owner.EnsureInstance();

        await module.DeactivateAsync(new ServiceCollection().BuildServiceProvider());
        activity.IsActive.Should().BeFalse();
        owner.IsOpen.Should().BeFalse();
        owner.EnsureInstance().Should().BeSameAs(connection, "Close keeps the shared instance for consumers");

        await module.ActivateAsync(new ServiceCollection().BuildServiceProvider());
        activity.IsActive.Should().BeTrue();
        owner.IsOpen.Should().BeTrue();
        owner.EnsureInstance().Should().BeSameAs(connection);
    }

    [Fact]
    public void SqliteConnectionOwner_EnsureInstance_ShouldNotOpen()
    {
        var owner = new SqliteConnectionOwner(() => "Data Source=:memory:");
        var connection = owner.EnsureInstance();
        owner.IsOpen.Should().BeFalse();
        connection.State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public void SqliteConnectionOwner_RequireOpen_WhenNotOpen_ShouldThrow()
    {
        var owner = new SqliteConnectionOwner(() => "Data Source=:memory:");
        owner.EnsureInstance();

        var act = () => owner.RequireOpen();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not open*");
    }

    [Fact]
    public void SqliteConnectionOwner_RequireOpen_WhenOpen_ShouldReturnConnection()
    {
        var owner = new SqliteConnectionOwner(() => "Data Source=:memory:");
        owner.Open();

        var connection = owner.RequireOpen();

        connection.State.Should().Be(System.Data.ConnectionState.Open);
        connection.Should().BeSameAs(owner.EnsureInstance());
    }
}
