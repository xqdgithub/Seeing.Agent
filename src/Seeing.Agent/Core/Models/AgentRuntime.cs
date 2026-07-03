namespace Seeing.Agent.Core.Models
{
    /// <summary>
    /// Agent 执行运行时类型
    /// </summary>
    public enum AgentRuntime
    {
        /// <summary>原生 Seeing.Agent 执行引擎（默认）</summary>
        Native = 0,

        /// <summary>ACP 透传模式，由外部 ACP 后端执行</summary>
        AcpPassthrough = 1
    }
}
