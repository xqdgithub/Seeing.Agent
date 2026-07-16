using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Core.Permission;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// Agent 装饰器基类 - 可叠加功能到 Agent
    /// </summary>
    public abstract class AgentDecorator : IAgent
    {
        /// <summary>被包装的内部 Agent</summary>
        protected readonly IAgent Inner;

        /// <summary>
        /// 创建装饰器
        /// </summary>
        protected AgentDecorator(IAgent inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public virtual string Name { get => Inner.Name; set => Inner.Name = value; }

        /// <inheritdoc />
        public virtual AgentMode Mode { get => Inner.Mode; set => Inner.Mode = value; }

        /// <inheritdoc />
        public virtual AgentStatus Status { get => Inner.Status; set => Inner.Status = value; }

        /// <inheritdoc />
        public virtual bool Disabled { get => Inner.Disabled; set => Inner.Disabled = value; }

        /// <inheritdoc />
        public virtual string Description { get => Inner.Description; set => Inner.Description = value; }

        /// <inheritdoc />
        public virtual string? SystemPrompt { get => Inner.SystemPrompt; set => Inner.SystemPrompt = value; }

        /// <inheritdoc />
        public virtual ModelReference? Model { get => Inner.Model; set => Inner.Model = value; }

        /// <inheritdoc />
        public virtual int? MaxSteps { get => Inner.MaxSteps; set => Inner.MaxSteps = value; }

        /// <inheritdoc />
        public virtual double? Temperature { get => Inner.Temperature; set => Inner.Temperature = value; }

        /// <inheritdoc />
        public virtual double? TopP { get => Inner.TopP; set => Inner.TopP = value; }

        /// <inheritdoc />
        public virtual int? MaxTokens { get => Inner.MaxTokens; set => Inner.MaxTokens = value; }

        /// <inheritdoc />
        public virtual IReadOnlyList<PermissionRuleEntry> PermissionRules { get => Inner.PermissionRules; set => Inner.PermissionRules = value; }

        /// <inheritdoc />
        public virtual IReadOnlyList<string> AllowedTools { get => Inner.AllowedTools; set => Inner.AllowedTools = value; }

        /// <inheritdoc />
        public virtual IReadOnlyList<string> DeniedTools { get => Inner.DeniedTools; set => Inner.DeniedTools = value; }

        /// <inheritdoc />
        public virtual PermissionEffect PermissionDefaultEffect { get => Inner.PermissionDefaultEffect; set => Inner.PermissionDefaultEffect = value; }

        /// <inheritdoc />
        public virtual AgentRuntime Runtime { get => Inner.Runtime; set => Inner.Runtime = value; }

        /// <inheritdoc />
        public virtual string? AcpBackend { get => Inner.AcpBackend; set => Inner.AcpBackend = value; }

        /// <inheritdoc />
        public virtual async IAsyncEnumerable<ChatMessage> ExecuteAsync(
            ChatMessage input,
            AgentContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var message in Inner.ExecuteAsync(input, context, cancellationToken))
            {
                yield return message;
            }
        }

        /// <summary>
        /// 获取内部 Agent
        /// </summary>
        public IAgent Unwrap() => Inner;

        /// <summary>
        /// 获取最内层的原始 Agent
        /// </summary>
        public IAgent GetInnermostAgent()
        {
            var current = Inner;
            while (current is AgentDecorator decorator)
            {
                current = decorator.Inner;
            }
            return current;
        }
    }
}