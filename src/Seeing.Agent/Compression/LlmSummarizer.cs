using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seeing.Agent.Abstractions.Agents;
using Seeing.Agent.Abstractions.Events;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Summarization;
using Seeing.Agent.Llm;
using Seeing.Session.Core;
using Seeing.Session.Management;
using System.Text;

namespace Seeing.Agent.Compression;

/// <summary>
/// 默认摘要器 - 基于 ITextCompletion 生成会话摘要
/// </summary>
/// <remarks>
/// prompt/model/provider 选择由本类内部自决（用户决策：压缩是框架的事）
/// <para>系统提示词优先复用内置 summary Agent 的定义（SystemPrompt），
/// 便于用户在 BuiltInAgents/自定义 Agent 中统一维护摘要风格；未找到时回退内置默认。</para>
/// </remarks>
public class LlmSummarizer : ISummarizer
{
    private const string SummaryAgentName = "summary";

    private const string FallbackSystemPrompt =
        """
你是一个会话压缩助手。将对话历史压缩为结构化摘要，保留关键信息，不要遗漏用户的核心意图，不要添加对话历史中不存在的信息。

严格按以下 Markdown 结构输出，保持章节顺序，不要输出模板标记：

## 目标
- [单句任务描述]

## 约束与偏好
- [用户的约束、偏好、规格说明，或"无"]

## 进度
### 已完成
- [已完成的工作，或"无"]

### 进行中
- [当前正在进行的工作，或"无"]

### 受阻
- [阻塞项，或"无"]

## 关键决策
- [决策及原因，或"无"]

## 下一步
- [按顺序排列的后续行动，或"无"]

## 关键上下文
- [重要的技术事实、错误信息、未决问题，或"无"]

## 相关文件
- [文件或目录路径及重要原因，或"无"]

规则：
- 保留所有章节，即使内容为空
- 使用简洁要点，不要使用散文段落
- 已知时保留精确的文件路径、命令、错误字符串和标识符
- 不要提及压缩过程或上下文已被压缩
- 如果对话以未回答的问题结束，保留该问题
- 如果对话以请求用户执行某操作结束，包含该请求
""";

    private readonly ITextCompletion _textCompletion;
    private readonly IAgentRegistry? _agentRegistry;
    private readonly ISessionManager? _sessionManager;
    private readonly ICompactionEventSink? _compactionEventSink;
    private readonly CompressionOptions _options;
    private readonly ILogger<LlmSummarizer> _logger;

    public LlmSummarizer(
        ITextCompletion textCompletion,
        CompressionOptions? options = null,
        IAgentRegistry? agentRegistry = null,
        ISessionManager? sessionManager = null,
        ICompactionEventSink? compactionEventSink = null,
        ILogger<LlmSummarizer>? logger = null)
    {
        _textCompletion = textCompletion ?? throw new ArgumentNullException(nameof(textCompletion));
        _options = options ?? new CompressionOptions();
        _agentRegistry = agentRegistry;
        _sessionManager = sessionManager;
        _compactionEventSink = compactionEventSink;
        _logger = logger ?? NullLogger<LlmSummarizer>.Instance;
    }

    /// <inheritdoc />
    public async Task<SummarizeResult> SummarizeAsync(
        SummarizeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_sessionManager == null)
        {
            throw new InvalidOperationException("未配置会话管理器（ISessionManager），无法执行摘要");
        }

        // 会话级细节全部由实现方自决：加载消息、模型、锚定摘要、保留策略、输出上限
        var session = await _sessionManager.GetOrLoadAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        // 压缩输入 = 活跃消息（不含历史已压缩标记的消息）
        var messages = session.GetActiveMessages();
        if (messages.Count == 0)
        {
            throw new InvalidOperationException("会话无消息，无需摘要");
        }

        // 对齐 opencode 压缩设计：对话历史作为消息列表传入（而非字符串拼接），
        // 压缩指令作为独立的最后一条 user 消息追加
        var llmMessages = ChatMessageHistoryBuilder.BuildHistory(messages);
        llmMessages.Add(new ChatMessage
        {
            Role = ChatRole.User,
            Content = BuildCompactionPrompt(
                session.GetContext<string>(SummarizeRequest.LastSummaryContextKey))
        });

        // 流式生成摘要：逐增量发布进度事件（summarizing 阶段），页面实时展示摘要生成过程，避免长时间无响应
        // 与正常助手消息一致：思考（推理）与正文分开流式返回，分别拼接与发布
        _compactionEventSink?.PublishDelta(request.SessionId, "summarizing");
        var summaryBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        await foreach (var update in _textCompletion.StreamCompleteAsync(
                           await ResolveSystemPromptAsync().ConfigureAwait(false),
                           llmMessages,
                           model: string.IsNullOrWhiteSpace(session.SelectedModel) ? null : session.SelectedModel,
                           // maxTokens 为 null 时不限制输出长度（未配置 SummaryTargetTokens 时保证压缩完整，不截断）
                           maxTokens: _options.SummaryTargetTokens,
                           ct: cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.ContentDelta))
            {
                summaryBuilder.Append(update.ContentDelta);
                _compactionEventSink?.PublishDelta(request.SessionId, "summarizing", update.ContentDelta);
            }

            if (!string.IsNullOrEmpty(update.ReasoningDelta))
            {
                reasoningBuilder.Append(update.ReasoningDelta);
                _compactionEventSink?.PublishDelta(request.SessionId, "summarizing",
                    contentDelta: null, reasoningDelta: update.ReasoningDelta);
            }
        }

        var summary = summaryBuilder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("摘要生成失败：模型返回空内容");
        }

        // 新历史 = 摘要消息（替代被压缩部分，信息不丢失） + 保留的最后一轮消息
        // 全量压缩：压缩结果即真实状态，活跃列表只含最新摘要 + 最后一条 user 及其后回复
        // （保留完整轮次可避免切断 assistant tool_call 与 tool 响应的配对，防止消息错乱）
        // 命令消息（/compact 等）不构成对话内容：必须压缩进历史，绝不允许出现在摘要之后
        var lastCommandIndex = messages.FindLastIndex(IsCommandMessage);
        var lastUserIndex = messages.FindLastIndex(m => m.Role == MessageRole.User && !IsCommandMessage(m));
        // 兜底：活跃消息无 user（如压缩后无新消息再次压缩、或纯 assistant 残留）时，
        // 至少跳过活跃段首（通常为旧摘要），保证新摘要插入旧摘要之后成为新的压缩真相，
        // 避免每次重复全量摘要却永不生效（摘要位置即真相，最后一个摘要才有效）
        var keepFrom = lastCommandIndex >= 0
            ? lastCommandIndex + 1
            : (lastUserIndex > 0 ? lastUserIndex : (lastUserIndex < 0 ? 1 : 0));
        var compactedCount = keepFrom;

        var resultMessages = new List<SessionMessage> { SessionMessage.AssistantMessage(summary) };
        resultMessages.AddRange(messages.Skip(keepFrom).Where(m => !IsCommandMessage(m)));

        return new SummarizeResult(
            Summary: summary,
            ResultMessages: resultMessages,
            SummaryTokenCount: Math.Max(1, (summary.Length) / 4),
            MessagesRemoved: compactedCount,
            Reasoning: reasoningBuilder.ToString().Trim());
    }

    /// <summary>
    /// 命令消息（内容以 / 开头的用户输入，如 /compact）不构成对话内容：
    /// 不参与保留轮次，压缩时一律归入历史，防止单独展示在摘要之后
    /// </summary>
    private static bool IsCommandMessage(SessionMessage message)
        => !string.IsNullOrEmpty(message.Content)
           && message.Content.TrimStart().StartsWith('/');

    /// <summary>
    /// 构建压缩指令（锚定摘要模式，对齐 opencode buildPrompt）：
    /// 有先前摘要时更新合并，否则创建新摘要
    /// </summary>
    private static string BuildCompactionPrompt(string? previousSummary)
    {
        if (string.IsNullOrWhiteSpace(previousSummary))
        {
            return "根据以上对话历史创建新的锚定摘要。";
        }

        return $"""
            根据以上对话历史更新锚定摘要。保留仍然正确的细节，移除过时的细节，并合并新的事实。

            <previous-summary>
            {previousSummary}
            </previous-summary>
            """;
    }

    /// <summary>
    /// 解析摘要系统提示词：优先使用内置 summary Agent 的 SystemPrompt，未找到或查询失败时回退默认
    /// </summary>
    private async Task<string> ResolveSystemPromptAsync()
    {
        if (_agentRegistry != null)
        {
            try
            {
                var summaryAgent = await _agentRegistry.GetAgentAsync(SummaryAgentName).ConfigureAwait(false);
                if (summaryAgent != null && !string.IsNullOrWhiteSpace(summaryAgent.SystemPrompt))
                {
                    return summaryAgent.SystemPrompt;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取 summary Agent 系统提示词失败，回退内置默认: {Error}", ex.Message);
            }
        }

        return FallbackSystemPrompt;
    }
}