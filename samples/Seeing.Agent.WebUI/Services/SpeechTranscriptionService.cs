using Microsoft.Extensions.Logging;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools.Support.Llm;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 未配置语音转写默认模型时抛出的可读异常。
/// </summary>
/// <remarks>
/// 用异常而不是返回码，是因为调用链只有两级（端点 → 服务），
/// 端点按下 <see cref="Reason"/> 映射成 503 + <c>code=modelNotConfigured</c>，
/// 用户据此去模型页补配置。
/// </remarks>
public sealed class SpeechTranscriptionUnavailableException : Exception
{
    /// <summary>可读原因（含要配置的键名与入口）。</summary>
    public string Reason { get; }

    /// <summary>用可读原因构造。</summary>
    public SpeechTranscriptionUnavailableException(string reason) : base(reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// WebUI 侧的语音转写门面：解析默认模型 → 经 <see cref="ILlmInvoker"/> 调
/// <see cref="ISpeechTranscriptionClient"/>。
/// </summary>
/// <remarks>
/// <para>
/// 走 <see cref="ILlmInvoker"/> 而不是直接取客户端，是为了让重试装饰器、<c>llm.*</c> Hook、
/// 用量统计对转写一致生效（与记忆的向量调用同一套路）。
/// </para>
/// <para>
/// <b>音频流所有权归调用方</b>（契约决议 17）：本类只读，不 Dispose。
/// 调用方须提供可 seek 的流——重试装饰器会 Seek 回起点。
/// </para>
/// </remarks>
public sealed class SpeechTranscriptionService
{
    private readonly ILlmInvoker _invoker;
    private readonly IModelConfigManager _models;
    private readonly ILogger<SpeechTranscriptionService> _logger;

    /// <summary>注入调用横切层、模型目录与日志。</summary>
    public SpeechTranscriptionService(
        ILlmInvoker invoker,
        IModelConfigManager models,
        ILogger<SpeechTranscriptionService> logger)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>转写一段音频，返回全文。</summary>
    /// <param name="sessionId">会话 ID（进 Hook 载荷与 <see cref="LlmInvocationOptions"/>）。</param>
    /// <param name="audio">音频流，可 seek；<b>所有权归调用方，本方法不释放</b>。</param>
    /// <param name="mimeType">音频 MIME（multipart 的 content-type）。</param>
    /// <param name="fileName">上传文件名（multipart 的 filename）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="SpeechTranscriptionUnavailableException">未配置语音转写默认模型。</exception>
    public async Task<string> TranscribeAsync(
        string sessionId,
        Stream audio,
        string? mimeType,
        string? fileName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(audio);

        var modelRef = _models.GetDefaultModel(LlmCapabilityKind.SpeechTranscription);
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            throw new SpeechTranscriptionUnavailableException(
                "未配置语音转写的默认模型：请在模型页的「默认模型（按能力）」区块为 " +
                "speechTranscription 选择模型。");
        }

        var wireModel = ModelReference.ToWireModelId(_models, modelRef);

        _logger.LogDebug(
            "语音转写开始: Session={SessionId}, Model={Model}, Mime={MimeType}",
            sessionId, modelRef, mimeType);

        var result = await _invoker.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
            modelRef,
            (client, call, token) => client.TranscribeAsync(
                new TranscriptionRequest
                {
                    Model = wireModel,
                    Audio = audio,
                    MimeType = mimeType,
                    FileName = fileName,
                },
                call,
                token),
            options: new LlmInvocationOptions { SessionId = sessionId },
            cancellationToken: ct);

        _logger.LogDebug(
            "语音转写完成: Session={SessionId}, Model={Model}, Chars={Chars}",
            sessionId, modelRef, result.Text?.Length ?? 0);

        return result.Text ?? string.Empty;
    }
}
