using System.Reflection;
using System.Text;
using FluentAssertions;
using Seeing.Agent.Abstractions.Execution;
using Xunit;

namespace Seeing.Agent.Tests.Execution;

public class ExecutionWorldContractsTests
{
    [Fact]
    public void IFileSystem_Should_Define_All_Members_As_Specified()
    {
        var t = typeof(IFileSystem);

        t.GetMethod(nameof(IFileSystem.ReadAllText), [typeof(string)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.ReadAllTextAsync), [typeof(string), typeof(CancellationToken)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.ReadLinesAsync), [typeof(string), typeof(CancellationToken)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.WriteAllText), [typeof(string), typeof(string)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.WriteAllTextAsync), [typeof(string), typeof(string), typeof(CancellationToken)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.Exists), [typeof(string)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.Delete), [typeof(string)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.EnumerateFiles), [typeof(string), typeof(string), typeof(bool)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.EnumerateDirectories), [typeof(string), typeof(string), typeof(bool)]).Should().NotBeNull();
        t.GetMethod(nameof(IFileSystem.GetFullPath), [typeof(string)]).Should().NotBeNull();
    }

    [Fact]
    public void ISubprocess_Should_Define_All_Members_As_Specified()
    {
        var t = typeof(ISubprocess);

        t.GetProperty(nameof(ISubprocess.Id)).Should().NotBeNull();
        t.GetProperty(nameof(ISubprocess.StandardOutput)).Should().NotBeNull();
        t.GetProperty(nameof(ISubprocess.StandardError)).Should().NotBeNull();
        t.GetProperty(nameof(ISubprocess.HasExited)).Should().NotBeNull();
        t.GetProperty(nameof(ISubprocess.ExitCode)).Should().NotBeNull();
        t.GetMethod(nameof(ISubprocess.WaitForExitAsync), [typeof(CancellationToken)]).Should().NotBeNull();
        t.GetMethod(nameof(ISubprocess.Kill), [typeof(bool)]).Should().NotBeNull();

        typeof(IDisposable).IsAssignableFrom(t).Should().BeTrue();
    }

    [Fact]
    public void ISubprocessFactory_Should_Define_Start()
    {
        var t = typeof(ISubprocessFactory);

        var start = t.GetMethod(nameof(ISubprocessFactory.Start), [typeof(SubprocessSpec)]);
        start.Should().NotBeNull();
        start!.ReturnType.Should().Be(typeof(ISubprocess));
    }

    [Fact]
    public void IExecutionWorld_Should_Expose_Cwd_FileSystem_And_Subprocess()
    {
        var t = typeof(IExecutionWorld);

        t.GetProperty(nameof(IExecutionWorld.Cwd))!.PropertyType.Should().Be(typeof(string));
        t.GetProperty(nameof(IExecutionWorld.FileSystem))!.PropertyType.Should().Be(typeof(IFileSystem));
        t.GetProperty(nameof(IExecutionWorld.Subprocess))!.PropertyType.Should().Be(typeof(ISubprocessFactory));
    }

    [Fact]
    public void SubprocessSpec_Should_Have_Expected_Defaults_When_Only_FileName_Set()
    {
        var spec = new SubprocessSpec { FileName = "git" };

        spec.FileName.Should().Be("git");
        spec.Arguments.Should().Be("");
        spec.WorkingDirectory.Should().BeNull();
        spec.Environment.Should().BeEmpty();
        spec.RedirectStandardOutput.Should().BeTrue();
        spec.RedirectStandardError.Should().BeTrue();
        spec.Encoding.Should().BeSameAs(Encoding.UTF8);
        spec.Timeout.Should().BeNull();
        spec.CancellationToken.Should().Be(default(CancellationToken));
    }
}
