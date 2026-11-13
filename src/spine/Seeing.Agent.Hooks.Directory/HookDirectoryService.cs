using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Configuration;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Hooks.Directory.Catalog;
using Seeing.Agent.Hooks.Directory.Dll;
using Seeing.Agent.Hooks.Directory.Scripts;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Hooks.Directory;

public sealed class HookDirectoryService : IHostedService
{
    private static readonly StringComparer PathKeys = StringComparer.FromComparison(PathBoundary.PathComparison);

    private static readonly Dictionary<string, string> LegacyRenames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["chat.params"] = "llm.params",
            ["chat.headers"] = "llm.headers",
            ["chat.message"] = "llm.message",
            ["chat.after_complete"] = "llm.after_complete",
            ["chat.on_error"] = "llm.on_error",
        };

    private readonly IHookManager _hooks;
    private readonly ISeeingDirectories _directories;
    private readonly IOptions<HookDirectoryOptions> _options;
    private readonly INodeHookInvoker _invoker;
    private readonly ILogger<HookDirectoryService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, ScriptSlot> _scripts = new(PathKeys);
    private readonly Dictionary<string, DllSlot> _dlls = new(PathKeys);
    private readonly List<object> _order = [];
    private readonly List<LoadedHookAssembly> _retired = [];
    private readonly HashSet<string> _noted = new(PathKeys);
    private int _started;
    private HookDirectoryWatcher? _watcher;

    public HookDirectoryService(
        IHookManager hooks,
        ISeeingDirectories directories,
        IOptions<HookDirectoryOptions> options,
        ILoggerFactory loggerFactory)
        : this(
            hooks,
            directories,
            options,
            new NodeHookClient(
                new SystemNodeRuntime(),
                () => HookWorkerFiles.Ensure(Path.Combine(directories.UserSeeingDirectory, "hooks", ".cache")),
                TimeProvider.System,
                loggerFactory.CreateLogger<NodeHookClient>()),
            loggerFactory.CreateLogger<HookDirectoryService>())
    {
    }

    internal HookDirectoryService(
        IHookManager hooks,
        ISeeingDirectories directories,
        IOptions<HookDirectoryOptions> options,
        INodeHookInvoker invoker,
        ILogger<HookDirectoryService> logger)
    {
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _directories = directories ?? throw new ArgumentNullException(nameof(directories));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled)
            return;

        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        _watcher = new HookDirectoryWatcher(HooksRoot(), SynchronizeAsync, TimeProvider.System, _logger);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        watcher?.Dispose();

        lock (_gate)
            RemoveOwned();

        await _invoker.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            Apply(Scan(HooksRoot(), cancellationToken));
        return Task.CompletedTask;
    }

    private string HooksRoot() => Path.Combine(_directories.UserSeeingDirectory, "hooks");

    private void Apply(List<HookSnapshotFile> snapshot)
    {
        var scripts = new List<HookSnapshotFile>();
        var dlls = new List<HookSnapshotFile>();
        foreach (var file in snapshot)
        {
            if (file.Disposition == HookFileDisposition.Script)
                scripts.Add(file);
            else if (file.Disposition == HookFileDisposition.EntryDll)
                dlls.Add(file);
        }

        // 没有脚本时不探测 Node。探测失败则这次差分里脚本列表为空，DLL 仍参与。
        var desired = new List<HookSnapshotFile>(dlls);
        if (scripts.Count > 0 && _invoker.EnsureAvailable())
            desired.AddRange(scripts);

        var diff = HookDirectoryCatalog.Diff(
            _scripts.Values.Select(slot => slot.Binding).ToList(),
            _dlls.Values.Select(slot => slot.Binding).ToList(),
            desired);

        // Remove* 是 PathKey，不是原始 FullPath。先摘后挂，禁止 Clear。
        foreach (var key in diff.RemoveScriptPaths)
            RemoveScript(key);
        foreach (var binding in diff.AddScripts)
            AddScript(binding);
        foreach (var key in diff.RemoveDllPaths)
            RemoveDll(key);
        foreach (var binding in diff.AddDlls)
            AddDll(binding);
    }

    private void AddScript(ScriptBinding binding)
    {
        var bridge = new HookScriptBridge(binding.FullPath, binding.Spec, binding.Priority, _invoker, _logger);
        _hooks.Register(bridge);
        _scripts[HookDirectoryCatalog.PathKey(binding.FullPath)] = new ScriptSlot(binding, bridge);
        _order.Add(bridge);
    }

    private void RemoveScript(string pathKey)
    {
        if (!_scripts.Remove(pathKey, out var slot))
            return;

        _hooks.Remove(slot.Bridge);
        _order.Remove(slot.Bridge);
    }

    private void AddDll(DllBinding binding)
    {
        var loaded = HookDllLoader.Load(binding.FullPath, Path.Combine(HooksRoot(), ".cache"), _logger);
        foreach (var handler in loaded.Handlers)
        {
            _hooks.Register(handler);
            _order.Add(handler);
        }

        foreach (var multi in loaded.Multis)
        {
            _hooks.RegisterMulti(multi);
            _order.Add(multi);
        }

        _dlls[HookDirectoryCatalog.PathKey(binding.FullPath)] = new DllSlot(binding, loaded);
    }

    private void RemoveDll(string pathKey)
    {
        if (!_dlls.Remove(pathKey, out var slot))
            return;

        foreach (var handler in slot.Loaded.Handlers)
        {
            _hooks.Remove(handler);
            _order.Remove(handler);
        }

        foreach (var multi in slot.Loaded.Multis)
        {
            _hooks.Remove(multi);
            _order.Remove(multi);
        }

        // 旧的加载上下文不卸载。丢掉引用会让调用方无法保住 LoadContext。
        _retired.Add(slot.Loaded);
    }

    private void RemoveOwned()
    {
        for (var i = _order.Count - 1; i >= 0; i--)
        {
            switch (_order[i])
            {
                case IMultiHookHandler multi:
                    _hooks.Remove(multi);
                    break;
                case IHookHandler handler:
                    _hooks.Remove(handler);
                    break;
            }
        }

        _order.Clear();
        foreach (var slot in _dlls.Values)
            _retired.Add(slot.Loaded);
        _dlls.Clear();
        _scripts.Clear();
    }

    private List<HookSnapshotFile> Scan(string root, CancellationToken cancellationToken)
    {
        var result = new List<HookSnapshotFile>();
        if (!System.IO.Directory.Exists(root))
            return result;

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var atRoot = PathBoundary.AreSame(dir, root);
            foreach (var entry in ListEntries(dir))
            {
                if (IsDirectory(entry))
                {
                    if (!atRoot)
                    {
                        Note(entry, LogLevel.Debug, "钩子目录跳过超过一层的嵌套目录 {Path}", entry);
                        continue;
                    }

                    var name = Path.GetFileName(entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (IsSkippedDirectory(name))
                    {
                        Note(entry, LogLevel.Debug, "钩子目录跳过 {Path}", entry);
                        continue;
                    }

                    if (!PathBoundary.IsWithin(entry, root))
                    {
                        _logger.LogWarning("钩子目录越出 hooks 根，已跳过: {Path}", entry);
                        continue;
                    }

                    stack.Push(entry);
                    continue;
                }

                if (!PathBoundary.IsWithin(entry, root))
                {
                    _logger.LogWarning("钩子文件越出 hooks 根，已跳过: {Path}", entry);
                    continue;
                }

                if (TryClassify(root, entry, out var file))
                    result.Add(file);
            }
        }

        return result;
    }

    private bool TryClassify(string root, string fullPath, out HookSnapshotFile file)
    {
        string? firstLine = null;
        string? hash = null;
        var legacy = false;
        try
        {
            if (IsJs(fullPath))
            {
                firstLine = ReadFirstLine(fullPath);
                legacy = NoteLegacy(root, fullPath);
            }
            else if (IsDll(fullPath))
            {
                hash = HashFile(fullPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "钩子文件读取失败，已跳过: {Path}", fullPath);
            file = null!;
            return false;
        }

        file = HookDirectoryCatalog.Classify(root, fullPath, withinBoundary: true, firstLine, hash);
        if (file.Disposition == HookFileDisposition.Ignored && !legacy)
            Note(fullPath, LogLevel.Debug, "钩子目录忽略 {Path}", fullPath);
        return true;
    }

    private bool NoteLegacy(string root, string fullPath)
    {
        var point = ScriptPoint(root, fullPath);
        if (point is null || !LegacyRenames.TryGetValue(point, out var renamed))
            return false;

        Note(
            "legacy:" + fullPath,
            LogLevel.Warning,
            "Hook 点 {Point} 已改名为 llm.*（现为 {Renamed}），本次注册不会触发，请改订阅新点",
            point,
            renamed);
        return true;
    }

    private static string? ScriptPoint(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var segments = relative.Split('/');
        if (segments.Length == 1)
            return Path.GetFileNameWithoutExtension(segments[0]);
        if (segments.Length == 2)
            return segments[0];
        return null;
    }

    private void Note(string key, LogLevel level, string message, params object[] args)
    {
        if (!_noted.Add(key))
            return;

        _logger.Log(level, message, args);
    }

    private List<string> ListEntries(string dir)
    {
        try
        {
            return System.IO.Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "钩子目录无法枚举，已跳过: {Path}", dir);
            return [];
        }
    }

    private static bool IsDirectory(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSkippedDirectory(string name) =>
        name.Length == 0
        || name[0] == '.'
        || name.Equals(".cache", StringComparison.Ordinal)
        || name.Equals("node_modules", StringComparison.Ordinal);

    private static bool IsJs(string path) =>
        Path.GetExtension(path).Equals(".js", StringComparison.OrdinalIgnoreCase);

    private static bool IsDll(string path) =>
        Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);

    private static string? ReadFirstLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadLine();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class ScriptSlot(ScriptBinding binding, HookScriptBridge bridge)
    {
        public ScriptBinding Binding { get; } = binding;
        public HookScriptBridge Bridge { get; } = bridge;
    }

    private sealed class DllSlot(DllBinding binding, LoadedHookAssembly loaded)
    {
        public DllBinding Binding { get; } = binding;
        public LoadedHookAssembly Loaded { get; } = loaded;
    }
}
