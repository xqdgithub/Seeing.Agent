using Microsoft.Extensions.Logging;
using Seeing.Agent.Commands.Attributes;
using System.Reflection;

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
    }
}