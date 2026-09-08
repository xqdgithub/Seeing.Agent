// 全局 using 声明 - 统一类型定义
// 所有 ChatMessage、ToolCall、ChatRole 等类型统一来自 Seeing.Agent.Abstractions.Llm 命名空间

// LLM 相关类型
global using ChatMessage = Seeing.Agent.Abstractions.Llm.ChatMessage;
global using ChatRequest = Seeing.Agent.Abstractions.Llm.ChatRequest;
global using ChatRole = Seeing.Agent.Abstractions.Llm.ChatRole;
global using FunctionCall = Seeing.Agent.Abstractions.Llm.FunctionCall;
global using FunctionDefinition = Seeing.Agent.Abstractions.Llm.FunctionDefinition;
global using StreamUpdate = Seeing.Agent.Abstractions.Llm.StreamUpdate;
global using TokenUsage = Seeing.Agent.Abstractions.Llm.TokenUsage;
global using ToolCall = Seeing.Agent.Abstractions.Llm.ToolCall;
global using ToolDefinition = Seeing.Agent.Abstractions.Llm.ToolDefinition;