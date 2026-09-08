using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Configuration;
using Seeing.Agent.Helpers;
using Seeing.Agent.Tools;
using System.Text.Json;

namespace Seeing.Agent.Decorators
{
    /// <summary>
    /// 工具超时装饰器 - 在工具执行漏斗内施加全局兜底超时。
    /// <para>
    /// 能力感知（调用时解析，非构造期固定值）：
    /// - <see cref="ToolCapabilityKeys.TimeoutSkip"/>="true" → 豁免兜底超时（如 TaskTool 子代理可长跑）；
    /// - <see cref="ToolCapabilityKeys.TimeoutBudget"/>（毫秒）→ 该工具自身上限，优先于全局兜底；
    /// - 均未声明 → 回落到 <see cref="SeeingAgentOptions.ToolExecutionTimeout"/>（IOptionsMonitor 实时读取，支持热重载；默认 null 关闭）。
    /// </para>
    /// <para>
    /// 超时语义：触发后返回 <see cref="ToolResult.Success"/>=false 的失败结果，Title="执行超时"，Error 描述超时时长，
    /// Metadata 标记 ["timeout"]=true 供上层区分"超时"与"业务失败"。工具内部吞掉取消返回结果的场景由此统一归一为超时。
    /// </para>
    /// </summary>
    public sealed class ToolTimeoutDecorator : ToolDecorator
    {
        private readonly IOptionsMonitor<SeeingAgentOptions> _optionsMonitor;
        private readonly ILogger _logger;

        /// <summary>
        /// 创建超时装饰器
        /// </summary>
        /// <param name="inner">被包装的工具</param>
        /// <param name="optionsMonitor">全局配置（IOptionsMonitor 支持热重载）</param>
        /// <param name="logger">日志器</param>
        public ToolTimeoutDecorator(
            ITool inner,
            IOptionsMonitor<SeeingAgentOptions> optionsMonitor,
            ILogger logger) : base(inner)
        {
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            // 能力在调用时解析：同一工具不同调用可有不同决策（如 timeout.budget 覆盖全局）
            var skipTimeout = ToolCapabilityReader.GetBool(Inner, ToolCapabilityKeys.TimeoutSkip);
            if (skipTimeout)
                return await base.ExecuteAsync(arguments, context).ConfigureAwait(false);

            var perToolBudget = ToolCapabilityReader.GetDurationMs(Inner, ToolCapabilityKeys.TimeoutBudget);
            var fallback = _optionsMonitor.CurrentValue.ToolExecutionTimeout;
            var effectiveTimeout = perToolBudget ?? fallback;

            if (effectiveTimeout is not { } timeout || timeout <= TimeSpan.Zero)
                return await base.ExecuteAsync(arguments, context).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            timeoutCts.CancelAfter(timeout);
            var timeoutToken = timeoutCts.Token;

            // 透视执行上下文：仅替换取消令牌，其余字段不变
            var timedContext = new ToolContext
            {
                SessionId = context.SessionId,
                MessageId = context.MessageId,
                CallId = context.CallId,
                Agent = context.Agent,
                CancellationToken = timeoutToken,
                MetadataSink = context.MetadataSink,
                EventSink = context.EventSink,
                PermissionChannel = context.PermissionChannel,
                Services = context.Services
            };

            try
            {
                var result = await base.ExecuteAsync(arguments, timedContext).ConfigureAwait(false);

                // 工具内部吞掉取消并返回结果，但实际是超时触发（外层取消未请求）：统一归一为"执行超时"
                if (timeoutCts.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "[ToolTimeout] 工具返回结果但超时已触发: Tool={ToolId}, Timeout={Timeout}s",
                        Id, timeout.TotalSeconds);

                    return TimeoutResult(timeout);
                }

                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                     && !context.CancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "[ToolTimeout] 工具执行超过超时: Tool={ToolId}, Timeout={Timeout}s",
                    Id, timeout.TotalSeconds);

                return TimeoutResult(timeout);
            }
        }

        /// <summary>构造统一超时失败结果（上层经 Metadata["timeout"] 识别为超时）。</summary>
        private static ToolResult TimeoutResult(TimeSpan timeout) => new()
        {
            Success = false,
            Title = "执行超时",
            Error = $"工具执行超过超时 {timeout.TotalSeconds} 秒",
            Metadata = new Dictionary<string, object> { ["timeout"] = true }
        };
    }
}
