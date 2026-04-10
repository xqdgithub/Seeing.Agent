// 全局 using 声明 - 统一类型定义
// 所有 ChatMessage、ToolCall、ChatRole 等类型统一来自 Seeing.Agent.Llm 命名空间

// LLM 相关类型
global using ChatMessage = Seeing.Agent.Llm.ChatMessage;
global using ToolCall = Seeing.Agent.Llm.ToolCall;
global using FunctionCall = Seeing.Agent.Llm.FunctionCall;
global using ChatRole = Seeing.Agent.Llm.ChatRole;
global using ChatRequest = Seeing.Agent.Llm.ChatRequest;
global using ChatResponse = Seeing.Agent.Llm.ChatResponse;
global using StreamUpdate = Seeing.Agent.Llm.StreamUpdate;
global using TokenUsage = Seeing.Agent.Llm.TokenUsage;
global using ToolDefinition = Seeing.Agent.Llm.ToolDefinition;
global using FunctionDefinition = Seeing.Agent.Llm.FunctionDefinition;
global using ChatContentPart = Seeing.Agent.Llm.ChatContentPart;