using FluentAssertions;
using Xunit;

namespace Seeing.Agent.Tools.Speech.Tests;

public class SpeechToolsModuleTests
{
    [Fact]
    public void Module_Id_ShouldBeSpeechTools()
        => new SpeechToolsModule().Id.Should().Be("speech.tools");

    [Fact]
    public void ProvidedTools_ShouldContainTranscribeAudio()
        => new SpeechToolsModule().ProvidedTools.Should().Equal("transcribe_audio");

    [Fact]
    public void ProvidedSeams_ShouldBeEmpty()
        => new SpeechToolsModule().ProvidedSeams.Should().BeEmpty();

    [Fact]
    public void DependsOn_ShouldRequireIoLocal()
        => new SpeechToolsModule().DependsOn.Should().Equal("io.local");
}
