using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Components;
namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// 默认执行上下文实现
    /// </summary>
    public class DefaultExecutionContext : IExecutionContext
    {
        private readonly IServiceProvider _services;
        private readonly ILogger _logger;
        private readonly IMetadataStore _metadata;

        /// <inheritdoc />
        public string SessionId { get; init; } = string.Empty;

        /// <inheritdoc />
        public string MessageId { get; init; } = string.Empty;

        /// <inheritdoc />
        public AgentDefinition? ActiveAgent { get; init; }

        /// <inheritdoc />
        public CancellationToken CancellationToken { get; init; }

        /// <inheritdoc />
        public IServiceProvider Services => _services;

        /// <inheritdoc />
        public ILogger Logger => _logger;

        /// <inheritdoc />
        public IMetadataStore Metadata => _metadata;

        /// <summary>
        /// 创建默认执行上下文
        /// </summary>
        public DefaultExecutionContext(
            IServiceProvider services,
            ILogger logger,
            IMetadataStore? metadata = null)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metadata = metadata ?? new ConcurrentMetadataStore();
        }

        /// <summary>
        /// 创建用于测试的执行上下文
        /// </summary>
        public static DefaultExecutionContext ForTest(
            string sessionId = "test-session",
            string messageId = "test-message",
            IServiceProvider? services = null)
        {
            var nullLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            var nullServices = services ?? new ServiceCollection().BuildServiceProvider();

            return new DefaultExecutionContext(nullServices, nullLogger)
            {
                SessionId = sessionId,
                MessageId = messageId
            };
        }
    }
}