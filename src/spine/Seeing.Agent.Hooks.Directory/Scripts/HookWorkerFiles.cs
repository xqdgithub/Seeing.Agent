using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Seeing.Files;

namespace Seeing.Agent.Hooks.Directory.Scripts;

public static class HookWorkerFiles
{
    public static string Ensure(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        var bytes = ReadEmbeddedBytes();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(cacheDirectory, "hook-worker-" + hash + ".js");
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return path;

        AtomicFileWrite.WriteAllTextAtomic(path, Encoding.UTF8.GetString(bytes), "hook-worker");
        return path;
    }

    private static byte[] ReadEmbeddedBytes()
    {
        var assembly = typeof(HookWorkerFiles).Assembly;
        var name = assembly.GetManifestResourceNames()
            .SingleOrDefault(candidate => candidate.EndsWith("hook-worker.js", StringComparison.Ordinal));
        if (name is null)
            throw new InvalidOperationException("缺少嵌入的钩子工人脚本 hook-worker.js。");

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"缺少嵌入的钩子工人脚本 {name}。");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
