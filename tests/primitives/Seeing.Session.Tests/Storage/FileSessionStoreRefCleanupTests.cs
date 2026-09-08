using FluentAssertions;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Session.Tests.Storage;

public class FileSessionStoreRefCleanupTests
{
    private static string NewSessionId() => "ses_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRefDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-ref-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(tempDir);
            var sessionId = NewSessionId();
            await store.SaveAsync(new Seeing.Session.Core.SessionData { Id = sessionId });

            var refDir = Path.Combine(tempDir, sessionId + ".ref");
            Directory.CreateDirectory(refDir);
            File.WriteAllText(Path.Combine(refDir, "abc.txt"), "data");
            Directory.Exists(refDir).Should().BeTrue();

            await store.DeleteAsync(sessionId);

            Directory.Exists(refDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
