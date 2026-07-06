using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Interfaces;
using System.Reflection;
using System.Runtime.Loader;

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// 插件加载器 - 负责解析 spec、加载程序集、实例化扩展
    /// <para>
    /// 参考 opencode PluginLoader 设计，支持：
    /// - NuGet 包（npm 风格）
    /// - 本地 DLL 文件
    /// </para>
    /// </summary>
    public class ExtensionLoader
    {
        private readonly ILogger<ExtensionLoader> _logger;
        private readonly string _cacheDirectory;

        /// <summary>
        /// 创建扩展加载器
        /// </summary>
        public ExtensionLoader(ILogger<ExtensionLoader> logger)
        {
            _logger = logger;
            _cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "plugins");
        }

        /// <summary>
        /// 解析插件 spec 为程序集路径
        /// </summary>
        /// <param name="spec">插件标识</param>
        /// <returns>程序集路径</returns>
        public async Task<string> ResolveTarget(string spec)
        {
            // file:// 或本地路径
            if (IsFileSpec(spec))
            {
                return ResolveFilePath(spec);
            }

            // NuGet 包 - 下载并缓存
            return await ResolveNuGetPackage(spec);
        }

        /// <summary>
        /// 判断是否为文件类型 spec
        /// </summary>
        private static bool IsFileSpec(string spec)
        {
            return spec.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                   spec.StartsWith("./", StringComparison.Ordinal) ||
                   spec.StartsWith("../", StringComparison.Ordinal) ||
                   spec.StartsWith("~", StringComparison.Ordinal) ||
                   Path.IsPathRooted(spec);
        }

        /// <summary>
        /// 解析文件路径
        /// </summary>
        private static string ResolveFilePath(string spec)
        {
            var path = spec;

            // 处理 file:// URL
            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                path = new Uri(path).LocalPath;
            }
            // 处理 ~ 用户主目录
            else if (path.StartsWith("~", StringComparison.Ordinal))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = path.Length == 1
                    ? userProfile
                    : Path.Combine(userProfile, path.Substring(1).TrimStart('/', '\\'));
            }

            // 转换为绝对路径（优先当前目录，其次应用程序基目录）
            if (!Path.IsPathRooted(path))
            {
                var cwdCandidate = Path.GetFullPath(path);
                if (File.Exists(cwdCandidate))
                    return cwdCandidate;

                var baseCandidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
                if (File.Exists(baseCandidate))
                    return baseCandidate;

                path = cwdCandidate;
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Extension file not found: {path}");
            }

            return path;
        }

        /// <summary>
        /// 解析 NuGet 包
        /// </summary>
        private async Task<string> ResolveNuGetPackage(string spec)
        {
            var (pkg, version) = ParseSpecifier(spec);

            var cacheDir = Path.Combine(_cacheDirectory, "packages", pkg);

            // 检查缓存
            if (Directory.Exists(cacheDir))
            {
                var dll = Directory.GetFiles(cacheDir, "*.dll", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                        .Equals(pkg.Split('/').Last(), StringComparison.OrdinalIgnoreCase));

                if (dll != null)
                {
                    _logger.LogDebug("Using cached extension: {Pkg}", pkg);
                    return dll;
                }
            }

            // TODO: 实现 NuGet 下载
            _logger.LogInformation("Downloading extension: {Pkg}@{Version}", pkg, version);
            await Task.Delay(100); // 占位，避免编译警告

            throw new NotSupportedException(
                $"NuGet extension download not implemented. Please use file path instead: {spec}");
        }

        /// <summary>
        /// 解析 spec 为包名和版本
        /// </summary>
        private static (string pkg, string version) ParseSpecifier(string spec)
        {
            // 处理 @scope/package@version 格式
            var atCount = spec.Count(c => c == '@');
            if (spec.StartsWith("@", StringComparison.Ordinal) && atCount >= 2)
            {
                var secondAt = spec.IndexOf('@', 1);
                return (spec.Substring(0, secondAt), spec.Substring(secondAt + 1));
            }

            // 处理 package@version 格式
            var lastAt = spec.LastIndexOf('@');
            if (lastAt > 0)
            {
                return (spec.Substring(0, lastAt), spec.Substring(lastAt + 1));
            }

            return (spec, "latest");
        }

        /// <summary>
        /// 从程序集加载扩展实例
        /// </summary>
        /// <param name="assemblyPath">程序集路径</param>
        /// <returns>扩展实例，如果未找到则返回 null</returns>
        public IExtension? LoadFromAssembly(string assemblyPath)
        {
            try
            {
                // 使用 AssemblyLoadContext 实现隔离（可选卸载）
                var loadContext = new ExtensionLoadContext(assemblyPath);
                var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

                var extensionType = assembly.GetTypes()
                    .FirstOrDefault(t => typeof(IExtension).IsAssignableFrom(t)
                                      && !t.IsInterface
                                      && !t.IsAbstract);

                if (extensionType == null)
                {
                    _logger.LogWarning("No IExtension implementation found in: {Path}", assemblyPath);
                    return null;
                }

                return (IExtension)Activator.CreateInstance(extensionType)!;
            }
            catch (ReflectionTypeLoadException ex)
            {
                _logger.LogError(ex, "Failed to load types from extension: {Path}. Loader exceptions: {Exceptions}",
                    assemblyPath, string.Join(", ", ex.LoaderExceptions.Select(e => e?.Message)));
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load extension from: {Path}", assemblyPath);
                return null;
            }
        }

        /// <summary>
        /// 加载外部插件
        /// </summary>
        /// <param name="specs">插件规格列表</param>
        /// <param name="context">扩展上下文</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>加载结果列表</returns>
        public async Task<List<ExtensionLoadResult>> LoadExternal(
            IEnumerable<PluginSpec> specs,
            ExtensionContext context,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ExtensionLoadResult>();

            foreach (var spec in specs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await LoadSingle(spec, context, cancellationToken);
                results.Add(result);
            }

            return results;
        }

        private async Task<ExtensionLoadResult> LoadSingle(
            PluginSpec spec,
            ExtensionContext context,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(spec.Spec))
            {
                return new ExtensionLoadResult
                {
                    Ok = false,
                    Stage = "entry",
                    Error = "Empty plugin spec"
                };
            }

            try
            {
                // 1. 解析目标路径
                string target;
                try
                {
                    target = await ResolveTarget(spec.Spec);
                }
                catch (Exception ex)
                {
                    return new ExtensionLoadResult
                    {
                        Ok = false,
                        Stage = "install",
                        Error = ex.Message
                    };
                }

                // 2. 加载程序集并获取扩展实例
                var instance = LoadFromAssembly(target);
                if (instance == null)
                {
                    return new ExtensionLoadResult
                    {
                        Ok = false,
                        Stage = "entry",
                        Error = $"No IExtension implementation in {spec.Spec}"
                    };
                }

                // 3. 解析 ID
                var id = instance.Id ?? ExtractIdFromSpec(spec.Spec);

                // 4. 构建元数据
                var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                var meta = new ExtensionMeta
                {
                    State = "first",
                    Id = id,
                    Source = IsFileSpec(spec.Spec) ? "file" : "npm",
                    Spec = spec.Spec,
                    Target = target,
                    LoadCount = 1,
                    FirstTime = now,
                    LastTime = now,
                    Fingerprint = $"{target}|{File.GetLastWriteTimeUtc(target):o}"
                };

                // 5. 初始化
                try
                {
                    await instance.InitializeAsync(context, meta);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Extension initialization failed: {Id}", id);
                    return new ExtensionLoadResult
                    {
                        Ok = false,
                        Stage = "init",
                        Error = ex.Message
                    };
                }

                return new ExtensionLoadResult
                {
                    Ok = true,
                    Loaded = new LoadedExtension
                    {
                        Id = id,
                        Spec = spec.Spec,
                        Source = meta.Source,
                        Target = target,
                        Instance = instance,
                        Options = spec.Options,
                        Meta = meta
                    }
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load extension: {Spec}", spec.Spec);
                return new ExtensionLoadResult
                {
                    Ok = false,
                    Stage = "load",
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// 从 spec 提取 ID
        /// </summary>
        private static string ExtractIdFromSpec(string spec)
        {
            if (IsFileSpec(spec))
            {
                var path = spec.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(spec).LocalPath
                    : spec;
                return Path.GetFileNameWithoutExtension(path);
            }

            var (pkg, _) = ParseSpecifier(spec);
            return pkg;
        }
    }

    /// <summary>
    /// 扩展加载上下文 - 支持程序集隔离
    /// </summary>
    internal class ExtensionLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;

        public ExtensionLoadContext(string pluginPath) : base(isCollectible: true)
        {
            if (File.Exists(pluginPath))
            {
                _resolver = new AssemblyDependencyResolver(pluginPath);
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName.Name))
                return null;

            // 优先复用宿主已加载的程序集，避免 first-party 扩展与 DI 注册类型身份不一致。
            var hostAssembly = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
            if (hostAssembly != null)
                return hostAssembly;

            // 回退到默认上下文（共享框架与 Seeing.* 包）
            if (assemblyName.Name.StartsWith("Seeing.", StringComparison.Ordinal) ||
                assemblyName.Name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            {
                return null;
            }

            var assemblyPath = _resolver?.ResolveAssemblyToPath(assemblyName);
            return assemblyPath != null ? LoadFromAssemblyPath(assemblyPath) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath != null ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
        }
    }
}