using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

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
        public virtual string Name => Inner.Name;

        /// <inheritdoc />
        public virtual AgentMode Mode => Inner.Mode;

        /// <inheritdoc />
        public virtual AgentStatus Status => Inner.Status;

        /// <inheritdoc />
        public virtual string Description => Inner.Description;

        /// <inheritdoc />
        public virtual string? SystemPrompt => Inner.SystemPrompt;

        /// <inheritdoc />
        public virtual ModelReference? Model => Inner.Model;

        /// <inheritdoc />
        public virtual int? MaxSteps => Inner.MaxSteps;

        /// <inheritdoc />
        public virtual IReadOnlyList<PermissionRule> Permissions => Inner.Permissions;

        /// <inheritdoc />
        public virtual IReadOnlyList<string> AllowedTools => Inner.AllowedTools;

        /// <inheritdoc />
        public virtual IReadOnlyList<string> DeniedTools => Inner.DeniedTools;

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