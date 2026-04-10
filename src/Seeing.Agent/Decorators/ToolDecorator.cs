using System.Text.Json;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// 工具装饰器基类 - 可叠加功能到工具
    /// </summary>
    public abstract class ToolDecorator : ITool
    {
        /// <summary>被包装的内部工具</summary>
        protected readonly ITool Inner;

        /// <summary>
        /// 创建装饰器
        /// </summary>
        protected ToolDecorator(ITool inner)
        {
            Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public virtual string Id => Inner.Id;

        /// <inheritdoc />
        public virtual string Description => Inner.Description;

        /// <inheritdoc />
        public virtual IReadOnlyList<string> Tags => Inner.Tags;

        /// <inheritdoc />
        public virtual ToolCategory Category => Inner.Category;

        /// <inheritdoc />
        public virtual JsonElement ParametersSchema => Inner.ParametersSchema;

        /// <inheritdoc />
        public virtual async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            return await Inner.ExecuteAsync(arguments, context);
        }

        /// <summary>
        /// 获取内部工具（用于解包）
        /// </summary>
        public ITool Unwrap() => Inner;

        /// <summary>
        /// 获取最内层的原始工具
        /// </summary>
        public ITool GetInnermostTool()
        {
            var current = Inner;
            while (current is ToolDecorator decorator)
            {
                current = decorator.Inner;
            }
            return current;
        }
    }
}
