using Microsoft.Extensions.Logging;
using Seeing.Agent.Commands.Attributes;
using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Commands.Discovery
{
    /// <summary>
    /// 反射发现的命令 - 包装带有 [Command] 注解的方法
    /// </summary>
    public class ReflectedCommand : ICommand
    {
        private readonly MethodInfo _method;
        private readonly object? _instance;
        private readonly ILogger? _logger;

        public CommandMetadata Metadata { get; }

        public ReflectedCommand(MethodInfo method, object? instance, CommandMetadata metadata, ILogger? logger = null)
        {
            _method = method;
            _instance = instance;
            _logger = logger;
            Metadata = metadata;
        }

        public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                // 解析参数
                var parameters = _method.GetParameters();
                var args = new object?[parameters.Length];

                for (var i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];

                    // 特殊参数：直接注入
                    if (param.ParameterType == typeof(CommandContext))
                    {
                        args[i] = context;
                    }
                    else if (param.ParameterType == typeof(CancellationToken))
                    {
                        args[i] = cancellationToken;
                    }
                    else if (param.ParameterType == typeof(IServiceProvider) && context.Services != null)
                    {
                        args[i] = context.Services;
                    }
                    else if (param.ParameterType == typeof(string) && param.Name == "args")
                    {
                        args[i] = context.Arguments;
                    }
                    else
                    {
                        // 尝试从 DI 容器获取
                        if (context.Services != null)
                        {
                            args[i] = context.Services.GetService(param.ParameterType);
                        }

                        // 如果 DI 中没有且参数可选，使用默认值
                        if (args[i] == null && param.HasDefaultValue)
                        {
                            args[i] = param.DefaultValue;
                        }

                        // 如果仍然没有值且参数必需，报错
                        if (args[i] == null && !param.HasDefaultValue)
                        {
                            return CommandResult.Fail($"缺少必需参数: {param.Name}");
                        }
                    }
                }

                // 执行方法
                var result = _method.Invoke(_instance, args);

                // 处理返回值
                if (result == null)
                {
                    return CommandResult.Ok();
                }

                if (result is Task<CommandResult> taskResult)
                {
                    return await taskResult;
                }

                if (result is Task task)
                {
                    await task;
                    return CommandResult.Ok();
                }

                if (result is CommandResult cmdResult)
                {
                    return cmdResult;
                }

                // 其他返回类型转为消息
                return CommandResult.Ok(result.ToString());
            }
            catch (TargetInvocationException tie)
            {
                var innerEx = tie.InnerException ?? tie;
                _logger?.LogError(innerEx, "命令执行失败: {CommandName}", Metadata.Name);
                return CommandResult.Fail(innerEx.Message);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "命令执行失败: {CommandName}", Metadata.Name);
                return CommandResult.Fail(ex.Message);
            }
        }
    }

    /// <summary>
    /// 命令发现器 - 扫描类型并发现带有 [Command] 注解的方法
    /// </summary>
    public class CommandDiscovery
    {
        private readonly ILoggerFactory? _loggerFactory;
        private readonly ILogger<CommandDiscovery>? _logger;

        public CommandDiscovery(ILoggerFactory? loggerFactory = null)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory?.CreateLogger<CommandDiscovery>();
        }

        /// <summary>
        /// 从类型发现命令
        /// </summary>
        public IEnumerable<ICommand> DiscoverFromType(Type type, object? instance = null)
        {
            var commands = new List<ICommand>();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

            foreach (var method in methods)
            {
                var cmdAttr = method.GetCustomAttribute<CommandAttribute>();
                if (cmdAttr == null) continue;

                var name = cmdAttr.Name ?? method.Name.ToLowerInvariant();
                var metadata = new CommandMetadata
                {
                    Name = name,
                    Description = cmdAttr.Description,
                    Aliases = cmdAttr.Aliases,
                    Usage = cmdAttr.Usage,
                    Category = cmdAttr.Category,
                    Examples = cmdAttr.Examples,
                    RequiresConfirmation = cmdAttr.RequiresConfirmation,
                    IsHidden = cmdAttr.IsHidden,
                    SortOrder = cmdAttr.SortOrder,
                    Source = type.FullName ?? type.Name
                };

                var cmdLogger = _loggerFactory?.CreateLogger($"{type.Name}.{method.Name}");
                var command = new ReflectedCommand(method, instance, metadata, cmdLogger);
                commands.Add(command);

                _logger?.LogDebug("发现命令: {Name} (来自 {Type}.{Method})", name, type.Name, method.Name);
            }

            return commands;
        }

        /// <summary>
        /// 从类型发现命令（泛型版本）
        /// </summary>
        public IEnumerable<ICommand> DiscoverFromType<T>(T? instance = null) where T : class
        {
            return DiscoverFromType(typeof(T), instance);
        }

        /// <summary>
        /// 从程序集发现所有命令
        /// </summary>
        public IEnumerable<ICommand> DiscoverFromAssembly(Assembly assembly, IServiceProvider? services = null)
        {
            var commands = new List<ICommand>();
            var types = assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<CommandProviderAttribute>() != null ||
                            t.GetMethods().Any(m => m.GetCustomAttribute<CommandAttribute>() != null));

            foreach (var type in types)
            {
                object? instance = null;

                // 对于非静态方法，尝试从 DI 获取实例
                if (services != null && !type.IsAbstract && !type.IsInterface)
                {
                    try
                    {
                        instance = services.GetService(type);
                        if (instance == null)
                        {
                            instance = Activator.CreateInstance(type);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "无法创建类型实例: {Type}", type.Name);
                    }
                }

                commands.AddRange(DiscoverFromType(type, instance));
            }

            return commands;
        }

        /// <summary>
        /// 从多个程序集发现命令
        /// </summary>
        public IEnumerable<ICommand> DiscoverFromAssemblies(IEnumerable<Assembly> assemblies, IServiceProvider? services = null)
        {
            return assemblies.SelectMany(a => DiscoverFromAssembly(a, services));
        }

        #region Markdown 命令发现

        private static readonly Regex FrontmatterRegex = new(
            @"^---\s*[\r]?\n(.*?)[\r]?\n---\s*[\r]?\n?",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        /// <summary>
        /// 从目录发现 Markdown 命令
        /// </summary>
        /// <param name="directory">搜索目录</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>发现的命令列表</returns>
        public async Task<IEnumerable<ICommand>> DiscoverFromMarkdownAsync(
            string directory,
            CancellationToken ct = default)
        {
            var commands = new List<ICommand>();

            if (!Directory.Exists(directory))
            {
                _logger?.LogDebug("Markdown 命令目录不存在: {Directory}", directory);
                return commands;
            }

            var files = Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories);
            _logger?.LogInformation("开始发现 Markdown 命令，目录: {Directory}, 文件数: {Count}", directory, files.Length);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var command = await ParseMarkdownCommandAsync(file, ct);
                    if (command != null)
                    {
                        commands.Add(command);
                        _logger?.LogDebug("发现 Markdown 命令: {Name} (来自 {File})", command.Metadata.Name, file);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "解析 Markdown 命令失败: {File}", file);
                }
            }

            _logger?.LogInformation("Markdown 命令发现完成，共 {Count} 个", commands.Count);
            return commands;
        }

        private async Task<ICommand?> ParseMarkdownCommandAsync(string filePath, CancellationToken ct)
        {
            var content = await File.ReadAllTextAsync(filePath, ct);
            var match = FrontmatterRegex.Match(content);

            if (!match.Success)
            {
                _logger?.LogDebug("Markdown 文件缺少 frontmatter: {File}", filePath);
                return null;
            }

            var frontmatter = _yamlDeserializer.Deserialize<CommandFrontmatter>(match.Groups[1].Value);
            var body = content.Substring(match.Length).Trim();

            if (string.IsNullOrEmpty(frontmatter.Name))
            {
                _logger?.LogDebug("Markdown 命令缺少 name 字段: {File}", filePath);
                return null;
            }

            var metadata = new CommandMetadata
            {
                Name = frontmatter.Name,
                Description = frontmatter.Description ?? "",
                Aliases = frontmatter.Aliases ?? Array.Empty<string>(),
                Category = ParseCategory(frontmatter.Category),
                Template = !string.IsNullOrEmpty(body) ? body : frontmatter.Template,
                Agent = frontmatter.Agent,
                Source = filePath
            };

            return new MarkdownCommand(metadata, _loggerFactory?.CreateLogger<MarkdownCommand>());
        }

        private static CommandCategory ParseCategory(string? category) => category?.ToLowerInvariant() switch
        {
            "basic" => CommandCategory.Basic,
            "navigation" => CommandCategory.Navigation,
            "agent" => CommandCategory.Agent,
            "tools" => CommandCategory.Tools,
            "system" => CommandCategory.System,
            "extension" => CommandCategory.Extension,
            _ => CommandCategory.Other
        };

        #endregion

        #region 内部类

        /// <summary>
        /// Markdown 文件命令
        /// </summary>
        private class MarkdownCommand : ICommand
        {
            public CommandMetadata Metadata { get; }
            private readonly string? _template;
            private readonly ILogger? _logger;

            public MarkdownCommand(CommandMetadata metadata, ILogger? logger = null)
            {
                Metadata = metadata;
                _template = metadata.Template;
                _logger = logger;
            }

            public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken = default)
            {
                if (string.IsNullOrEmpty(_template))
                {
                    return Task.FromResult(CommandResult.Fail("命令模板为空"));
                }

                try
                {
                    var output = RenderTemplate(_template, context.Arguments);
                    return Task.FromResult(CommandResult.Ok(output));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Markdown 命令执行失败: {Name}", Metadata.Name);
                    return Task.FromResult(CommandResult.Fail(ex.Message));
                }
            }

            private static string RenderTemplate(string template, string args)
            {
                var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                return template
                    .Replace("{{1}}", parts.ElementAtOrDefault(0) ?? "")
                    .Replace("{{2}}", parts.ElementAtOrDefault(1) ?? "")
                    .Replace("{{arguments}}", args)
                    .Replace("{{args}}", args);
            }
        }

        /// <summary>
        /// YAML frontmatter 数据结构
        /// </summary>
        private class CommandFrontmatter
        {
            public string? Name { get; set; }
            public string? Description { get; set; }
            public string? Template { get; set; }
            public string[]? Aliases { get; set; }
            public string? Category { get; set; }
            public string? Agent { get; set; }
        }

        #endregion
    }
}