using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 执行上下文接口 - 所有操作的统一上下文
    /// </summary>
    public interface IExecutionContext
    {
        /// <summary>会话 ID</summary>
        string SessionId { get; }

        /// <summary>消息 ID</summary>
        string MessageId { get; }

        /// <summary>当前活动的 Agent</summary>
        IAgent? ActiveAgent { get; }

        /// <summary>取消令牌</summary>
        CancellationToken CancellationToken { get; }

        /// <summary>服务提供者 - 获取所需服务</summary>
        IServiceProvider Services { get; }

        /// <summary>日志器</summary>
        ILogger Logger { get; }

        /// <summary>元数据存储 - 线程安全</summary>
        IMetadataStore Metadata { get; }

        /// <summary>权限请求通道</summary>
        IPermissionChannel PermissionChannel { get; }

        /// <summary>追踪器 - 分布式追踪</summary>
        IActivityTracer? Tracer { get; }
    }

    /// <summary>
    /// 活动追踪器接口 - 分布式追踪支持
    /// </summary>
    public interface IActivityTracer
    {
        /// <summary>追踪 ID</summary>
        string TraceId { get; }

        /// <summary>跨度 ID</summary>
        string SpanId { get; }

        /// <summary>开始新的跨度</summary>
        IDisposable StartSpan(string name);

        /// <summary>记录事件</summary>
        void AddEvent(string name, KeyValuePair<string, object>? tag = null);

        /// <summary>记录异常</summary>
        void RecordException(Exception ex);
    }
}