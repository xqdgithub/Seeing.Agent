using FluentAssertions;
using Seeing.Agent.Core.Snapshot;
using Xunit;

namespace Seeing.Agent.Tests.Snapshot;

public class DiffCalculatorTests
{
    private readonly DiffCalculator _calculator = new();

    [Fact]
    public void ComputeDiff_NoChanges_ShouldReturnAllEqual()
    {
        // Arrange
        var text1 = "line1\nline2\nline3";
        var text2 = "line1\nline2\nline3";

        // Act
        var diff = _calculator.ComputeDiff(text1, text2);

        // Assert
        diff.Should().HaveCount(3);
        diff.All(d => d.Operation == DiffOperation.Equal).Should().BeTrue();
    }

    [Fact]
    public void ComputeDiff_AddedLine_ShouldDetectInsert()
    {
        // Arrange
        var text1 = "line1\nline3";
        var text2 = "line1\nline2\nline3";

        // Act
        var diff = _calculator.ComputeDiff(text1, text2);

        // Assert
        diff.Should().Contain(d => d.Operation == DiffOperation.Insert && d.Content == "line2");
    }

    [Fact]
    public void ComputeDiff_RemovedLine_ShouldDetectDelete()
    {
        // Arrange
        var text1 = "line1\nline2\nline3";
        var text2 = "line1\nline3";

        // Act
        var diff = _calculator.ComputeDiff(text1, text2);

        // Assert
        diff.Should().Contain(d => d.Operation == DiffOperation.Delete && d.Content == "line2");
    }

    [Fact]
    public void ComputeDiff_ModifiedLine_ShouldDetectDeleteAndInsert()
    {
        // Arrange
        var text1 = "line1\nold\nline3";
        var text2 = "line1\nnew\nline3";

        // Act
        var diff = _calculator.ComputeDiff(text1, text2);

        // Assert
        diff.Should().Contain(d => d.Operation == DiffOperation.Delete && d.Content == "old");
        diff.Should().Contain(d => d.Operation == DiffOperation.Insert && d.Content == "new");
    }

    [Fact]
    public void ApplyPatch_ShouldReconstructText()
    {
        // Arrange
        var text1 = "line1\nline2\nline3";
        var text2 = "line1\nmodified\nline3";
        var diff = _calculator.ComputeDiff(text1, text2);

        // Act
        var result = _calculator.ApplyPatch(text1, diff);

        // Assert
        result.Should().Be(text2);
    }

    [Fact]
    public void SerializeDeserialize_ShouldRoundTrip()
    {
        // Arrange
        var text1 = "line1\nline2\nline3";
        var text2 = "line1\nnew\nline3";
        var diff = _calculator.ComputeDiff(text1, text2);

        // Act
        var serialized = _calculator.SerializePatch(diff);
        var deserialized = _calculator.DeserializePatch(serialized);

        // Assert
        deserialized.Should().HaveCount(diff.Count);
        for (int i = 0; i < diff.Count; i++)
        {
            deserialized[i].Operation.Should().Be(diff[i].Operation);
            deserialized[i].Content.Should().Be(diff[i].Content);
        }
    }

    [Fact]
    public void ToUnifiedDiff_ShouldIncludeHeaders()
    {
        // Arrange
        var text1 = "line1\nline2";
        var text2 = "line1\nmodified";

        // Act
        var unified = _calculator.ToUnifiedDiff("test.txt", text1, text2);

        // Assert
        unified.Should().Contain("--- test.txt");
        unified.Should().Contain("+++ test.txt");
        unified.Should().Contain("@@");
    }
}

public class SnapshotModelTests
{
    [Fact]
    public void Snapshot_Defaults()
    {
        // Arrange & Act
        var snapshot = new Seeing.Agent.Core.Snapshot.Snapshot();

        // Assert
        snapshot.Id.Should().NotBeEmpty();
        snapshot.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        snapshot.IsFullSnapshot.Should().BeTrue();
    }

    [Fact]
    public void Snapshot_WithDiff_ShouldNotBeFull()
    {
        // Arrange & Act
        var snapshot = new Seeing.Agent.Core.Snapshot.Snapshot
        {
            BaseSnapshotId = "prev-id",
            DiffPatches = "--- old\n+++ new\n"
        };

        // Assert
        snapshot.IsFullSnapshot.Should().BeFalse();
    }

    [Fact]
    public void SnapshotDiff_Defaults()
    {
        // Arrange & Act
        var diff = new SnapshotDiff();

        // Assert
        diff.AddedLines.Should().Be(0);
        diff.DeletedLines.Should().Be(0);
        diff.UnchangedLines.Should().Be(0);
    }

    [Fact]
    public void SnapshotOptions_Defaults()
    {
        // Arrange & Act
        var options = new SnapshotOptions();

        // Assert
        options.MaxSnapshotsPerFile.Should().Be(50);
        options.MaxAge.Should().Be(TimeSpan.FromDays(30));
    }
}
