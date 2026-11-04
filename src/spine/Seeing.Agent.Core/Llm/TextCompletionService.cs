using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using System.Runtime.CompilerServices;

namespace Seeing.Agent.Core.Llm;

/// <summary>
/// <see cref="ITextCompletion"/> 默认实现：经 <see cref="ILlmService.CompleteRawAsync"/> 旁路补全，不触发 Hook。
/// <para>
/// 未显式指定模型时取 <c>GetDefaultModel(Chat)</c>——严格查表，未配置即抛错，
/// 不回落其他能力、不查目录猜测（决议 13 / spec §13 原则 1）。
/// </para>
/// </summary>
public sealed class TextCompletionService : ITextCompletion
{
    private readonly ILlmService _llm;
    private readonly IModelConfigManager _models;
    private readonly ILogger<TextCompletionService>? _logger;

    /// <summary>
    /// 注入 LLM 服务与模型目录构造文本补全服务。
    /// </summary>
    public TextCompletionService(
        ILlmService llm,
        IModelConfigManager models,
        ILogger<TextCompletionService>? logger = null)
    {
        _llm = llm;
        _models = models;
        _logger = logger;
    }

    /// <summary>
    /// 未显式指定 maxTokens 时的默认输出 token 上限。
    /// </summary>
    public const int DefaultMaxTokens = 4096;

    /// <summary>
    /// 以单条用户提示发起旁路文本补全（温度 0，不触发 Hook）。
    /// </summary>
    public Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string? model = null,
        int? maxTokens = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = userPrompt }
        };
        return CompleteAsync(systemPrompt, messages, model, maxTokens, ct);
    }

    /// <summary>
    /// 以消息列表发起旁路补全（温度 0，走 CompleteRawAsync 不触发 Hook），返回裁剪后的正文。
    /// </summary>
    public async Task<string> CompleteAsync(
        string systemPrompt,
        List<ChatMessage> messages,
        string? model = null,
        int? maxTokens = null,
        CancellationToken ct = default)
    {
        var modelId = ResolveModel(model);

        var request = new ChatRequest
        {
            Model = modelId,
            SystemPrompt = systemPrompt,
            Temperature = 0,
            MaxTokens = maxTokens is > 0 ? maxTokens.Value : DefaultMaxTokens,
            Messages = messages
        };

        var response = await _llm.CompleteRawAsync(modelId, request, ct).ConfigureAwait(false);
        var text = response.Message.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            _logger?.LogWarning(
                "ITextCompletion empty content: Model={Model}, MaxTokens={MaxTokens}, ReasoningLen={ReasoningLen}",
                modelId,
                request.MaxTokens,
                response.Message.ReasoningContent?.Length ?? 0);
        }
        return text;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamUpdate> StreamCompleteAsync(
        string systemPrompt,
        List<ChatMessage> messages,
        string? model = null,
        int? maxTokens = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var modelId = ResolveModel(model);

        var request = new ChatRequest
        {
            Model = modelId,
            SystemPrompt = systemPrompt,
            Temperature = 0,
            MaxTokens = maxTokens is > 0 ? maxTokens.Value : DefaultMaxTokens,
            Messages = messages
        };

        // 透传完整增量（正文 + 推理内容），由调用方决定如何展示
        await foreach (var update in _llm.CompleteRawStreamAsync(modelId, request, ct).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// 显式模型优先；缺省取 chat 能力默认值。未配置即抛错——旁路调用方（标题/摘要/记忆抽取）
    /// 需要确定性地知道自己用了哪个模型，静默换模型比报错更难排查。
    /// </summary>
    private string ResolveModel(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return model.Trim();

        var defaultModel = _models.GetDefaultModel(LlmCapabilityKind.Chat);
        if (string.IsNullOrWhiteSpace(defaultModel))
        {
            var chatKey = LlmCapabilityMap.ToConfigKey(LlmCapabilityKind.Chat);
            throw new InvalidOperationException(
                $"未配置文本补全模型：请显式传入 model，或设置 SeeingAgent:DefaultModels:{chatKey}。");
        }

        return defaultModel;
    }
}
