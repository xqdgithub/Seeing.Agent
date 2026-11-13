using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Files;

namespace Seeing.Agent.Hooks.Directory.Dll;

public enum HookDllSkip
{
    None,
    Companion,
    Failed,
}

public sealed class LoadedHookAssembly
{
    public required string SourcePath { get; init; }

    public required string CachePath { get; init; }

    public required string ContentHash { get; init; }

    public IReadOnlyList<IHookHandler> Handlers { get; init; } = [];

    public IReadOnlyList<IMultiHookHandler> Multis { get; init; } = [];

    public HookDllSkip Skip { get; init; }

    public string? Message { get; init; }

    /// <summary>调用方必须保住此引用。上下文本身不卸载。</summary>
    public AssemblyLoadContext? LoadContext { get; init; }
}

public static class HookDllLoader
{
    public static LoadedHookAssembly Load(string sourceDllPath, string cacheDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDllPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(sourceDllPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "钩子 DLL 读取失败: {Path}", sourceDllPath);
            return Failed(sourceDllPath, "", "", ex.Message, null);
        }

        var hash = Hash(bytes);
        var cachePath = Path.Combine(cacheDirectory, hash + ".dll");
        try
        {
            if (!CacheMatches(cachePath, hash))
                AtomicFileWrite.WriteAllBytesAtomic(cachePath, bytes, "hook-dll", logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "钩子 DLL 影子复制失败: {Path}", sourceDllPath);
            return Failed(sourceDllPath, cachePath, hash, ex.Message, null);
        }

        // 不调用 Unload。入口只锁影子副本，原文件在 Windows 上才能删掉。
        // 托管依赖与原生库都从源路径解析：Resolving 优先默认上下文同名程序集，否则 AssemblyDependencyResolver；
        // ResolvingUnmanagedDll 用同一 resolver 的 ResolveUnmanagedDllToPath，再 NativeLibrary.Load。
        var context = new HookAssemblyLoadContext(hash, sourceDllPath);

        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(Path.GetFullPath(cachePath));
        }
        catch (BadImageFormatException ex)
        {
            // 根目录的原生 / 非托管 .dll 不是坏掉的钩子程序集。
            logger.LogDebug(ex, "钩子 DLL 不是托管程序集，按伴随程序集跳过: {Path}", sourceDllPath);
            return Companion(sourceDllPath, cachePath, hash, context);
        }
        catch (Exception ex) when (ex is FileLoadException or FileNotFoundException)
        {
            logger.LogWarning(ex, "钩子 DLL 加载失败: {Path}", sourceDllPath);
            return Failed(sourceDllPath, cachePath, hash, ex.Message, context);
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            logger.LogWarning(ex, "钩子 DLL 反射失败: {Path}", sourceDllPath);
            return Failed(sourceDllPath, cachePath, hash, ex.Message, context);
        }

        var handlers = new List<IHookHandler>();
        var multis = new List<IMultiHookHandler>();
        foreach (var type in types)
        {
            if (!CanConstruct(type))
                continue;

            var isMulti = typeof(IMultiHookHandler).IsAssignableFrom(type);
            var isSingle = typeof(IHookHandler).IsAssignableFrom(type);
            if (!isMulti && !isSingle)
                continue;

            object instance;
            try
            {
                instance = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException($"无法创建 {type.FullName}");
                if (isMulti)
                    multis.Add((IMultiHookHandler)instance);
                else
                    handlers.Add((IHookHandler)instance);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "钩子 DLL 反射失败: {Path}", sourceDllPath);
                return Failed(sourceDllPath, cachePath, hash, ex.Message, context);
            }
        }

        if (handlers.Count == 0 && multis.Count == 0)
        {
            logger.LogDebug("钩子 DLL 没有合格处理器，按伴随程序集跳过: {Path}", sourceDllPath);
            return Companion(sourceDllPath, cachePath, hash, context);
        }

        return new LoadedHookAssembly
        {
            SourcePath = sourceDllPath,
            CachePath = cachePath,
            ContentHash = hash,
            Handlers = handlers,
            Multis = multis,
            LoadContext = context,
        };
    }

    private static bool CacheMatches(string cachePath, string contentHash)
    {
        if (!File.Exists(cachePath))
            return false;

        return string.Equals(Hash(File.ReadAllBytes(cachePath)), contentHash, StringComparison.Ordinal);
    }

    internal static Assembly? ResolveDependency(
        AssemblyLoadContext context,
        AssemblyDependencyResolver resolver,
        AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.Name))
            return null;

        var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var path = resolver.ResolveAssemblyToPath(name);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }

    private static bool CanConstruct(Type type)
    {
        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            return false;
        if (!type.IsPublic && !type.IsNestedPublic)
            return false;
        return type.GetConstructor(Type.EmptyTypes) is not null;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static LoadedHookAssembly Companion(
        string sourcePath,
        string cachePath,
        string contentHash,
        AssemblyLoadContext context) =>
        new()
        {
            SourcePath = sourcePath,
            CachePath = cachePath,
            ContentHash = contentHash,
            Skip = HookDllSkip.Companion,
            LoadContext = context,
        };

    private static LoadedHookAssembly Failed(
        string sourcePath,
        string cachePath,
        string contentHash,
        string message,
        AssemblyLoadContext? context) =>
        new()
        {
            SourcePath = sourcePath,
            CachePath = cachePath,
            ContentHash = contentHash,
            Skip = HookDllSkip.Failed,
            Message = message,
            LoadContext = context,
        };
}

/// <summary>
/// 钩子入口的加载上下文。托管解析仍优先默认上下文里的同名程序集；
/// 原生库从源 DLL 的 <see cref="AssemblyDependencyResolver"/> 解析。
/// </summary>
internal sealed class HookAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>已挂到 <see cref="ResolvingUnmanagedDll"/> 的委托。测试核对接线，并可直接调用。</summary>
    internal Func<Assembly, string, IntPtr> UnmanagedResolver { get; }

    internal int UnmanagedResolveAttempts { get; private set; }

    internal HookAssemblyLoadContext(string contextName, string sourceDllPath)
        : base(contextName, isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(sourceDllPath);
        Resolving += (_, name) => HookDllLoader.ResolveDependency(this, _resolver, name);
        UnmanagedResolver = ResolveUnmanaged;
        ResolvingUnmanagedDll += UnmanagedResolver;
    }

    private IntPtr ResolveUnmanaged(Assembly _, string unmanagedDllName)
    {
        UnmanagedResolveAttempts++;
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
    }
}
