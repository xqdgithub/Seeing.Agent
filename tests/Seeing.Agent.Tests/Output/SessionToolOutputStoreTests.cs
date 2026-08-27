using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Output;
using Seeing.Session.Storage;
using Xunit;

namespace Seeing.Agent.Tests.Output;

public class SessionToolOutputStoreTests
{
    private static (string TempDir, FileSessionStore Store) CreateTempStore()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "seeing-output-store-" + Guid.NewGuid().ToString("N"));
        return (tempDir, new FileSessionStore(tempDir));
    }

    [Fact]
    public async Task SaveAsync_ShouldWriteFullContentAndReturnPath()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            var path = await outputStore.SaveAsync("ses_a", "call_1", "hello\nworld", CancellationToken.None);

            path.Should().Be(Path.Combine(tempDir, "ses_a.ref", "call_1.txt"));
            File.ReadAllText(path).Should().Be("hello\nworld");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ShouldSanitizeIllegalFileNameChars()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            var path = await outputStore.SaveAsync("ses_a", "bad/name:1", "data", CancellationToken.None);

            path.Should().StartWith(Path.Combine(tempDir, "ses_a.ref"));
            Path.GetFileName(path).Should().NotContain(":").And.NotContain("/").And.NotContain("\\");
            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Be("data");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetRefDirectory_ShouldUseStoreBaseDirectory()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            outputStore.GetRefDirectory("ses_a").Should().Be(Path.Combine(tempDir, "ses_a.ref"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DeleteSessionRefDirectory_ShouldRemoveDirectory()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            var refDir = outputStore.GetRefDirectory("ses_a");
            Directory.CreateDirectory(refDir);
            File.WriteAllText(Path.Combine(refDir, "x.txt"), "x");
            Directory.Exists(refDir).Should().BeTrue();

            outputStore.DeleteSessionRefDirectory("ses_a");

            Directory.Exists(refDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSaves_ShouldWriteDistinctFiles()
    {
        var (tempDir, store) = CreateTempStore();
        try
        {
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);
            var tasks = Enumerable.Range(1, 10)
                .Select(i => outputStore.SaveAsync("ses_a", $"call_{i}", $"content-{i}", CancellationToken.None));

            var paths = await Task.WhenAll(tasks);

            paths.Distinct().Should().HaveCount(10);
            foreach (var (path, i) in paths.Select((p, idx) => (p, idx + 1)))
            {
                File.ReadAllText(path).Should().Be($"content-{i}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetRefDirectory_ShouldFollowRelocation()
    {
        var tempA = Path.Combine(Path.GetTempPath(), "seeing-reloc-a-" + Guid.NewGuid().ToString("N"));
        var tempB = Path.Combine(Path.GetTempPath(), "seeing-reloc-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStore(tempA);
            var outputStore = new SessionToolOutputStore(store, NullLogger<SessionToolOutputStore>.Instance);

            outputStore.GetRefDirectory("ses_a").Should().Be(Path.Combine(tempA, "ses_a.ref"));

            store.SetBaseDirectory(tempB);
            outputStore.GetRefDirectory("ses_a").Should().Be(Path.Combine(tempB, "ses_a.ref"));
        }
        finally
        {
            foreach (var dir in new[] { tempA, tempB })
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
