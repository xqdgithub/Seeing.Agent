using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Core.Hooks;
using Seeing.Agent.Hooks.Directory.Dll;
using Seeing.Files;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookDllLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "seeing-hook-dll-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
        if (System.IO.Directory.Exists(_root))
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不回收的加载上下文会锁住 .cache 里的影子副本，Windows 上删目录抛 UnauthorizedAccessException。
        }
    }

    [Fact]
    public async Task Load_ShouldRunSampleHookAfterDeletingOriginal()
    {
        var sample = CopyHandlers("sample.dll");
        var loaded = HookDllLoader.Load(sample, CacheDir(), NullLogger.Instance);

        loaded.Skip.Should().Be(HookDllSkip.None, loaded.Message);
        File.Delete(sample);
        File.Exists(sample).Should().BeFalse();

        var handler = loaded.Handlers.Single(h => h.GetType().Name == "SampleHook");
        handler.Spec.Should().Be(HookRegistry.ToolExecuteBefore);
        handler.Priority.Should().Be(40);
        var result = await handler.ExecuteAsync(
            HookPayload.Blocking(
                HookRegistry.ToolExecuteBefore,
                "sess",
                cancellationToken: TestContext.Current.CancellationToken));
        result.Continue.Should().BeTrue();
        loaded.LoadContext.Should().NotBeNull();
    }

    [Fact]
    public void Load_ShouldRewriteCacheWhenExistingBytesDiffer()
    {
        var sample = CopyHandlers("sample.dll");
        var source = File.ReadAllBytes(sample);
        var hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        var cacheDir = CacheDir();
        var cachePath = Path.Combine(cacheDir, hash + ".dll");
        AtomicFileWrite.WriteAllBytesAtomic(cachePath, [1, 2, 3], "hook-dll-test");

        var loaded = HookDllLoader.Load(sample, cacheDir, NullLogger.Instance);

        loaded.Skip.Should().Be(HookDllSkip.None, loaded.Message);
        loaded.ContentHash.Should().Be(hash);
        Path.GetFullPath(loaded.CachePath).Should().Be(Path.GetFullPath(cachePath));
        File.ReadAllBytes(loaded.CachePath).Should().Equal(source);
    }

    [Fact]
    public void Load_EmptyAssembly_ShouldSkipAsCompanionAtDebug()
    {
        var extra = Copy("extra.dll", FixturePaths.EmptyDll());
        var logger = new ListLogger();

        var loaded = HookDllLoader.Load(extra, CacheDir(), logger);

        loaded.Skip.Should().Be(HookDllSkip.Companion, loaded.Message);
        loaded.Handlers.Should().BeEmpty();
        loaded.Multis.Should().BeEmpty();
        logger.Debugs.Should().ContainSingle();
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_ThrowingHook_ShouldStopBlockingTrigger()
    {
        var sample = CopyHandlers("sample.dll");
        var loaded = HookDllLoader.Load(sample, CacheDir(), NullLogger.Instance);
        var throwing = loaded.Handlers.Single(h => h.GetType().Name == "ThrowingHook");
        var manager = new HookManager(NullLogger<HookManager>.Instance);
        manager.Register(throwing);

        var result = await manager.TriggerBlockingAsync(
            HookRegistry.ToolExecuteBefore,
            "sess",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Continue.Should().BeFalse();
        result.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Load_MultiAndSingle_ShouldAppearOnceInMultis()
    {
        var sample = CopyHandlers("sample.dll");
        var loaded = HookDllLoader.Load(sample, CacheDir(), NullLogger.Instance);

        loaded.Skip.Should().Be(HookDllSkip.None, loaded.Message);
        loaded.Multis.Should().ContainSingle(m => m.GetType().Name == "MultiAndSingleHook");
        loaded.Handlers.Select(h => h.GetType().Name).Should().NotContain("MultiAndSingleHook");
        loaded.Handlers.Select(h => h.GetType().Name)
            .Concat(loaded.Multis.Select(m => m.GetType().Name))
            .Count(name => name == "MultiAndSingleHook")
            .Should().Be(1);
    }

    private string CacheDir() => Path.Combine(_root, "hooks", ".cache");

    private string CopyHandlers(string name) => Copy(name, FixturePaths.HandlerDll());

    private string Copy(string name, string source)
    {
        var dest = Path.Combine(_root, "hooks", name);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
        return dest;
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Debugs { get; } = [];

        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Debug)
                Debugs.Add(message);
            else if (logLevel == LogLevel.Warning)
                Warnings.Add(message);
        }
    }
}
