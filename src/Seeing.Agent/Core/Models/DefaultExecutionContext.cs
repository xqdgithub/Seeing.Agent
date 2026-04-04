using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;

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
        private readonly IPermissionChannel _permissionChannel;

        /// <inheritdoc />
        public string SessionId { get; init; } = string.Empty;

        /// <inheritdoc />
        public string MessageId { get; init; } = string.Empty;

        /// <inheritdoc />
        public IAgent? ActiveAgent { get; init; }

        /// <inheritdoc />
        public CancellationToken CancellationToken { get; init; }

        /// <inheritdoc />
        public IServiceProvider Services => _services;

        /// <inheritdoc />
        public ILogger Logger => _logger;

        /// <inheritdoc />
        public IMetadataStore Metadata => _metadata;

        /// <inheritdoc />
        public IPermissionChannel PermissionChannel => _permissionChannel;

        /// <inheritdoc />
        public IActivityTracer? Tracer { get; init; }

        /// <summary>
        /// 创建默认执行上下文
        /// </summary>
        public DefaultExecutionContext(
            IServiceProvider services,
            ILogger logger,
            IPermissionChannel permissionChannel,
            IMetadataStore? metadata = null)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _permissionChannel = permissionChannel ?? throw new ArgumentNullException(nameof(permissionChannel));
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
            var permissionChannel = new DefaultPermissionChannel();
            
            return new DefaultExecutionContext(nullServices, nullLogger, permissionChannel)
            {
                SessionId = sessionId,
                MessageId = messageId
            };
        }
    }

    /// <summary>
    /// 默认权限通道实现
    /// </summary>
    public class DefaultPermissionChannel : IPermissionChannel
    {
        private Func<PermissionRequest, Task<bool>>? _handler;

        /// <inheritdoc />
        public Task<bool> RequestConfirmationAsync(PermissionRequest request)
        {
            if (_handler != null)
                return _handler(request);
            
            // 默认行为：自动批准
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public void SetConfirmationHandler(Func<PermissionRequest, Task<bool>> handler)
        {
            _handler = handler;
        }
    }
}