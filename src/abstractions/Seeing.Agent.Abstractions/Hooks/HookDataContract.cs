using Seeing.Agent.Abstractions.Llm;

namespace Seeing.Agent.Abstractions.Hooks;

/// <summary>
/// Hook 数据契约 - 每个 Hook 点的字段定义
/// </summary>
public static class HookDataContract
{
    // ===== agent.before_invoke =====
    public static class AgentBeforeInvoke
    {
        public static readonly DataField<string> AgentName = new("agentName", Required: true, Description: "Agent 名称");
        public static readonly DataField<string> Mode = new("mode", Required: true, Description: "Agent 模式");
        public static readonly DataField<bool> IsTopLevel = new("isTopLevel", Required: true, Description: "是否顶层");

        public static readonly DataField<string?> SystemPrompt = new("systemPrompt", Mutable: true, Description: "系统提示词");
        public static readonly DataField<double> Temperature = new("temperature", Mutable: true, DefaultValue: 0.7);
        public static readonly DataField<int> MaxTokens = new("maxTokens", Mutable: true, DefaultValue: 4096);
    }

    // ===== agent.after_invoke =====
    public static class AgentAfterInvoke
    {
        public static readonly DataField<string> AgentName = new("agentName", Required: true);
        public static readonly DataField<bool> Success = new("success", Required: true);
        public static readonly DataField<string?> Error = new("error");

        public static readonly DataField<int> TotalSteps = new("totalSteps", InResult: true);
        public static readonly DataField<object?> TotalUsage = new("totalUsage", InResult: true);
        public static readonly DataField<double> Duration = new("duration", InResult: true);
    }

    // ===== llm.after_complete =====
    // result 是「超集」（spec §7.6）：通用键恒有，能力特化键按 capability 出现。
    // 组装位置在 Core 的 ILlmHookPayloadAdapter 实现，订阅方（含插件）只读本处常量，禁止手拼字符串。
    public static class LlmAfterComplete
    {
        // ----- 通用键（全部能力恒有） -----

        /// <summary>能力名，点分格式（<c>chat</c> / <c>speech.synthesis</c>），值出自 <c>LlmCapabilityMap.ToHookValue</c>。</summary>
        public static readonly DataField<string> Capability = new("capability", Required: true, InResult: true, Description: "能力名（点分）");

        /// <summary>Provider ID。</summary>
        public static readonly DataField<string> Provider = new("provider", Required: true, InResult: true);

        /// <summary>模型 ID（spec §7.6 的规范键；<see cref="ModelId"/> 是它的历史别名）。</summary>
        public static readonly DataField<string> Model = new("model", Required: true, InResult: true);

        /// <summary>
        /// 模型 ID 的历史别名：改名前的订阅方读的是 <c>modelId</c>，
        /// 适配器同时写出，避免运行期静默丢字段（与 <see cref="Model"/> 同值）。
        /// </summary>
        public static readonly DataField<string> ModelId = new("modelId", InResult: true, Description: "model 的历史别名");

        /// <summary>token 用量；非 chat 能力只进载荷、不写会话预算（决议 1）。</summary>
        public static readonly DataField<TokenUsage?> Usage = new("usage", InResult: true);

        /// <summary>调用耗时（spec §7.6 定为 TimeSpan）。</summary>
        public static readonly DataField<TimeSpan> Duration = new("duration", InResult: true);

        // ----- chat 键 -----

        /// <summary>响应消息 ID；流式时取自首个 chunk 的 Id。</summary>
        public static readonly DataField<string?> MessageId = new("messageId", InResult: true);

        /// <summary>是否来自流式调用。</summary>
        public static readonly DataField<bool> Streaming = new("streaming", InResult: true, DefaultValue: false);

        /// <summary>执行轮次序号；无归属时为 null。</summary>
        public static readonly DataField<int?> Step = new("step", InResult: true);

        /// <summary>推理内容（仅 chat）。</summary>
        public static readonly DataField<string?> Reasoning = new("reasoning", InResult: true);

        /// <summary>工具调用列表（仅 chat），<c>IReadOnlyList&lt;ToolCall&gt;</c>。</summary>
        public static readonly DataField<object?> ToolCalls = new("toolCalls", InResult: true);

        /// <summary>原始 <c>ChatResponse</c>（仅 chat、仅非流式）：既有记账方读它取 usage，属兼容键。</summary>
        public static readonly DataField<object?> Response = new("response", InResult: true);

        // ----- chat / transcription 共用键 -----

        /// <summary>文本正文：chat 的回复正文或转写全文。</summary>
        public static readonly DataField<string?> Content = new("content", InResult: true);

        // ----- embedding 键 -----

        /// <summary>返回的向量条数。</summary>
        public static readonly DataField<int> VectorCount = new("vectorCount", InResult: true);

        /// <summary>向量维度（取首条）。</summary>
        public static readonly DataField<int> Dimensions = new("dimensions", InResult: true);

        // ----- decision 键 -----

        /// <summary>按问题名索引的应答，<c>IReadOnlyDictionary&lt;string, DecisionAnswer&gt;</c>。</summary>
        public static readonly DataField<object?> Answers = new("answers", InResult: true);

        // ----- speech.synthesis 键 -----

        /// <summary>合成音频字节数（不搬字节本身：Hook 载荷不是缓冲区）。</summary>
        public static readonly DataField<int> AudioBytes = new("audioBytes", InResult: true);
    }

    // ===== tool.execute.after =====
    public static class ToolExecuteAfter
    {
        public static readonly DataField<string> ToolId = new("toolId", Required: true);
        public static readonly DataField<string> CallId = new("callId", Required: true);
        public static readonly DataField<string> SessionId = new("sessionId");
        public static readonly DataField<object?> Arguments = new("args");

        public static readonly DataField<string?> Output = new("output", InResult: true);
        public static readonly DataField<string?> Error = new("error", InResult: true);
        public static readonly DataField<bool> Success = new("success", InResult: true, DefaultValue: true);
        public static readonly DataField<double> Duration = new("duration", InResult: true);
    }

    // ===== subagent.started =====
    public static class SubAgentStarted
    {
        public static readonly DataField<string> ParentSessionId = new("parentSessionId", Required: true);
        public static readonly DataField<string> SubSessionId = new("subSessionId", Required: true);
        public static readonly DataField<string> SubAgentName = new("subAgentName", Required: true);
        public static readonly DataField<string?> Prompt = new("prompt");
    }

    // ===== loop.detected =====
    public static class LoopDetected
    {
        public static readonly DataField<string> ToolId = new("toolId", Required: true);
        public static readonly DataField<int> ConsecutiveCount = new("consecutiveCount", Required: true);
        public static readonly DataField<string> RecommendedAction = new("recommendedAction", Required: true);
    }
}