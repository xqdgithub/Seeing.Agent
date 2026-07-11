using Microsoft.Extensions.Logging;
using Seeing.Agent.Llm;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话压缩服务 - 使用 AI 生成历史摘要
/// </summary>
public class SessionCompactionService
{
    private readonly ILlmService _llmService;
    private readonly ILogger<SessionCompactionService>? _logger;
    private readonly string _compactionModel;
    private readonly int _keepLastN;

    private const string SummaryPromptTemplate = """
        请将以下会话历史压缩为简洁的摘要，保留关键信息：
        1. 用户的主要需求和目标
        2. 已完成的关键任务和决策
        3. 重要的上下文信息（如文件路径、配置等）
        4. 未解决的问题或待办事项
        要求：使用中文，语言简洁，保留关键细节，控制在 500 字以内。
        会话历史：
        {history}
        """;

    public SessionCompactionService(
        ILlmService llmService,
        ILogger<SessionCompactionService>? logger = null,
        string? compactionModel = null,
        int keepLastN = 10)
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _logger = logger;
        _compactionModel = compactionModel ?? GetDefaultCompactionModel(llmService);
        _keepLastN = keepLastN;
    }

    /// <summary>
    /// 压缩会话历史
    /// </summary>
    public async Task<CompactionResult> CompactAsync(
        IReadOnlyList<SessionMessage> messages,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        // 空列表保护
        if (messages == null || messages.Count == 0)
            return new CompactionResult(Success: false, Message: "消息列表为空");

        // 消息数量检查
        if (messages.Count <= _keepLastN)
            return new CompactionResult(Success: false, Message: $"消息数量较少（{messages.Count} 条），无需压缩");

        var headCount = messages.Count - _keepLastN;
        var tailMessages = messages.Skip(headCount).ToList();

        // 调用 LLM 生成摘要
        string summary;
        try
        {
            var prompt = BuildSummaryPrompt(messages.Take(headCount).ToList());
            var request = new ChatRequest
            {
                Messages = new List<ChatMessage> { new() { Role = ChatRole.User, Content = prompt } },
                MaxTokens = 1000,
                Temperature = 0.3
            };
            var response = await _llmService.CompleteAsync(_compactionModel, request, sessionId, cancellationToken);
            summary = response.Message?.Content ?? "(摘要生成失败)";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AI 压缩失败");
            return new CompactionResult(Success: false, Error: ex.Message);
        }

        // 创建摘要消息
        var summaryMessage = new SessionMessage
        {
            Id = $"compaction_{Guid.NewGuid():N}",
            Role = MessageRole.System,
            Content = $"📝 **会话历史摘要**（压缩了 {headCount} 条消息）\n\n{summary}",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["is_compaction_summary"] = true,
                ["compressed_count"] = headCount
            }
        };

        return new CompactionResult(
            Success: true,
            SummaryMessage: summaryMessage,
            TailMessages: tailMessages,
            CompressedCount: headCount
        );
    }

    /// <summary>
    /// 获取默认压缩模型（优先使用低成本模型）
    /// </summary>
    private static string GetDefaultCompactionModel(ILlmService llmService)
    {
        var models = llmService.GetAvailableModels();
        if (models.Count == 0)
            throw new InvalidOperationException("LLM 服务没有配置任何可用模型");

        // 优先选择低成本模型
        var preferred = new[] { "gpt-4o-mini", "gpt-3.5-turbo", "claude-3-haiku", "deepseek-chat" };
        foreach (var name in preferred)
            if (models.ContainsKey(name)) return name;

        return models.Keys.First();
    }

    /// <summary>
    /// 构建摘要提示词
    /// </summary>
    private static string BuildSummaryPrompt(IReadOnlyList<SessionMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                MessageRole.User => "👤 用户",
                MessageRole.Assistant => "🤖 助手",
                _ => "⚙️ 系统"
            };
            var content = msg.Content.Length > 500 ? msg.Content[..500] + "..." : msg.Content;
            sb.AppendLine($"{role}: {content}");
        }
        return SummaryPromptTemplate.Replace("{history}", sb.ToString());
    }
}

/// <summary>
/// 压缩结果
/// </summary>
public record CompactionResult(
    bool Success,
    string? Error = null,
    string? Message = null,
    SessionMessage? SummaryMessage = null,
    List<SessionMessage>? TailMessages = null,
    int CompressedCount = 0
);
