using FluentAssertions;
using Xunit;

namespace Seeing.IO.Local.Tests;

public class LocalFileSystemBinaryWriteTests
{
    [Fact]
    public async Task WriteAllBytesAsync_ShouldWriteExactBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"local-{Guid.NewGuid():N}.bin");
        try
        {
            await new LocalFileSystem().WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });

            File.ReadAllBytes(path).Should().Equal(1, 2, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
