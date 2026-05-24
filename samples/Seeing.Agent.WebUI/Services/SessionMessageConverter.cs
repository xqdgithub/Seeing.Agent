using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services
{
    /// <summary>
    /// SessionMessage 与 ChatMessage 双向转换器
    /// <para>
    /// 用于将核心库的 SessionMessage 转换为 LLM 层的 ChatMessage，
    /// 以及将 ChatMessage 转换回 SessionMessage 进行存储。
    /// </para>
    /// </summary>
    public static class SessionMessageConverter
    {
        /// <summary>
        /// 将 SessionMessage 列表转换为 ChatMessage 列表
        /// </summary>
        /// <param name="sessionMessages">Session 消息列表</param>
        /// <returns>LLM 消息列表</returns>
        public static List<ChatMessage> ToChatMessages(IEnumerable<SessionMessage> sessionMessages)
        {
            return sessionMessages.Select(ToChatMessage).ToList();
        }

        /// <summary>
        /// 将单个 SessionMessage 转换为 ChatMessage
        /// </summary>
        public static ChatMessage ToChatMessage(SessionMessage sessionMessage)
        {
            var chatMessage = new ChatMessage
            {
                Role = sessionMessage.Role,
                Content = sessionMessage.Content,
                ReasoningContent = sessionMessage.ReasoningContent,
                ToolCallId = sessionMessage.ToolCallId
            };

            // 转换多模态内容段
            if (sessionMessage.Parts != null && sessionMessage.Parts.Count > 0)
            {
                chatMessage.Parts = sessionMessage.Parts
                    .Select(ToChatContentPart)
                    .ToList();
            }

            // 转换工具调用
            if (sessionMessage.ToolCalls != null && sessionMessage.ToolCalls.Count > 0)
            {
                chatMessage.ToolCalls = sessionMessage.ToolCalls
                    .Select(ToToolCall)
                    .ToList();
            }

            return chatMessage;
        }

        /// <summary>
        /// 将 ChatMessage 列表转换为 SessionMessage 列表
        /// </summary>
        /// <param name="chatMessages">LLM 消息列表</param>
        /// <returns>Session 消息列表</returns>
        public static List<SessionMessage> ToSessionMessages(IEnumerable<ChatMessage> chatMessages)
        {
            return chatMessages.Select(ToSessionMessage).ToList();
        }

        /// <summary>
        /// 将单个 ChatMessage 转换为 SessionMessage
        /// </summary>
        public static SessionMessage ToSessionMessage(ChatMessage chatMessage)
        {
            var sessionMessage = new SessionMessage
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Role = chatMessage.Role,
                Content = chatMessage.Content,
                ReasoningContent = chatMessage.ReasoningContent,
                ToolCallId = chatMessage.ToolCallId,
                CreatedAt = DateTime.Now
            };

            // 转换多模态内容段
            if (chatMessage.Parts != null && chatMessage.Parts.Count > 0)
            {
                sessionMessage.Parts = chatMessage.Parts
                    .Select(ToSessionContentPart)
                    .ToList();
            }

            // 转换工具调用
            if (chatMessage.ToolCalls != null && chatMessage.ToolCalls.Count > 0)
            {
                sessionMessage.ToolCalls = chatMessage.ToolCalls
                    .Select(ToSessionToolCall)
                    .ToList();
            }

            return sessionMessage;
        }

        /// <summary>
        /// 将 SessionContentPart 转换为 ChatContentPart
        /// </summary>
        private static ChatContentPart ToChatContentPart(SessionContentPart sessionPart)
        {
            return new ChatContentPart
            {
                Type = sessionPart.Type,
                Text = sessionPart.Text,
                Url = sessionPart.Url,
                DataBase64 = sessionPart.DataBase64,
                MimeType = sessionPart.MimeType,
                FileName = sessionPart.FileName,
                FileId = sessionPart.FileId,
                ImageDetail = sessionPart.ImageDetail
            };
        }

        /// <summary>
        /// 将 ChatContentPart 转换为 SessionContentPart
        /// </summary>
        private static SessionContentPart ToSessionContentPart(ChatContentPart chatPart)
        {
            return new SessionContentPart
            {
                Type = chatPart.Type,
                Text = chatPart.Text,
                Url = chatPart.Url,
                DataBase64 = chatPart.DataBase64,
                MimeType = chatPart.MimeType,
                FileName = chatPart.FileName,
                FileId = chatPart.FileId,
                ImageDetail = chatPart.ImageDetail
            };
        }

        /// <summary>
        /// 将 SessionToolCall 转换为 ToolCall
        /// </summary>
        private static ToolCall ToToolCall(SessionToolCall sessionToolCall)
        {
            return new ToolCall
            {
                Id = sessionToolCall.Id,
                Type = sessionToolCall.Type,
                Function = new FunctionCall
                {
                    Name = sessionToolCall.Name,
                    Arguments = sessionToolCall.Arguments
                }
            };
        }

        /// <summary>
        /// 将 ToolCall 转换为 SessionToolCall
        /// </summary>
        private static SessionToolCall ToSessionToolCall(ToolCall toolCall)
        {
            return new SessionToolCall
            {
                Id = toolCall.Id,
                Type = toolCall.Type,
                Name = toolCall.Function?.Name ?? "",
                Arguments = toolCall.Function?.Arguments ?? "{}",
                Status = "completed"
            };
        }

        /// <summary>
        /// 创建用户 SessionMessage
        /// </summary>
        public static SessionMessage CreateUserMessage(string content)
        {
            return SessionMessage.UserMessage(content);
        }

        /// <summary>
        /// 创建助手 SessionMessage（带推理内容）
        /// </summary>
        public static SessionMessage CreateAssistantMessage(string content, string? reasoning = null)
        {
            if (!string.IsNullOrEmpty(reasoning))
            {
                return SessionMessage.AssistantMessageWithReasoning(content, reasoning);
            }
            return SessionMessage.AssistantMessage(content);
        }

        /// <summary>
        /// 创建工具响应 SessionMessage
        /// </summary>
        public static SessionMessage CreateToolMessage(string content, string toolCallId, string? toolName = null)
        {
            return SessionMessage.ToolMessage(content, toolCallId, toolName);
        }

        /// <summary>
        /// 创建带工具调用的助手 SessionMessage
        /// </summary>
        public static SessionMessage CreateAssistantMessageWithToolCalls(
            List<ToolCall> toolCalls,
            string? content = null)
        {
            var sessionToolCalls = toolCalls.Select(ToSessionToolCall).ToList();
            return SessionMessage.AssistantMessageWithToolCalls(sessionToolCalls, content);
        }
    }
}