namespace Seeing.Agent.Core.Hooks;

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
    
    // ===== chat.after_complete =====
    public static class ChatAfterComplete
    {
        public static readonly DataField<string> ModelId = new("modelId", Required: true);
        public static readonly DataField<string> Provider = new("provider");
        public static readonly DataField<string?> MessageId = new("messageId");
        public static readonly DataField<bool> Streaming = new("streaming", DefaultValue: false);
        public static readonly DataField<int?> Step = new("step");
        
        public static readonly DataField<string?> Content = new("content", InResult: true);
        public static readonly DataField<string?> Reasoning = new("reasoning", InResult: true);
        public static readonly DataField<object?> Usage = new("usage", InResult: true);
        public static readonly DataField<object?> ToolCalls = new("toolCalls", InResult: true);
        public static readonly DataField<double> Duration = new("duration", InResult: true);
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