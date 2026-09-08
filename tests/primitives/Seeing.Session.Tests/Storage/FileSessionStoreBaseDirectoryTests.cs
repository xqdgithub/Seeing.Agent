using FluentAssertions;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class FileSessionStoreBaseDirectoryTests
{
    [Fact]
    public void BaseDirectory_ShouldReturnConstructedPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-base-dir-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(tempDir);
            store.BaseDirectory.Should().Be(tempDir);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BaseDirectory_ShouldUpdateAfterRelocation()
    {
        var tempA = Path.Combine(Path.GetTempPath(), "seeing-base-a-" + Guid.NewGuid().ToString("N"));
        var tempB = Path.Combine(Path.GetTempPath(), "seeing-base-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(tempA);
            store.SetBaseDirectory(tempB);
            store.BaseDirectory.Should().Be(tempB);
        }
        finally
        {
            foreach (var dir in new[] { tempA, tempB })
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
