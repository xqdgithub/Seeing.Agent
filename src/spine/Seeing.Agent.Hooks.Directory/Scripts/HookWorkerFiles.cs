using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Seeing.Files;

namespace Seeing.Agent.Hooks.Directory.Scripts;

public static class HookWorkerFiles
{
    // 工人落在 hooks/.cache/*.js。祖先 package.json 的 "type":"module" 会让 node 把入口当 ESM，进程起不来。
    // .cache/package.json 是更近的那一份，把入口固定成 CommonJS。用户脚本不在这个目录里，另由工人 _compile。
    private const string CommonJsPackage = "{\"type\":\"commonjs\"}\n";

    public static string Ensure(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        var bytes = ReadEmbeddedBytes();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(cacheDirectory, "hook-worker-" + hash + ".js");
        EnsureCommonJsPackage(cacheDirectory);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return path;

        AtomicFileWrite.WriteAllTextAtomic(path, Encoding.UTF8.GetString(bytes), "hook-worker");
        return path;
    }

    private static void EnsureCommonJsPackage(string cacheDirectory)
    {
        var packagePath = Path.Combine(cacheDirectory, "package.json");
        if (File.Exists(packagePath) && File.ReadAllText(packagePath) == CommonJsPackage)
            return;

        AtomicFileWrite.WriteAllTextAtomic(packagePath, CommonJsPackage, "hook-worker-package");
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
