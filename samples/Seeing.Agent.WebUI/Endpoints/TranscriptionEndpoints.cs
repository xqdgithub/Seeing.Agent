using Microsoft.AspNetCore.Mvc;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.WebUI.Models;
using Seeing.Agent.WebUI.Services;
using Seeing.Session.Core;

namespace Seeing.Agent.WebUI.Endpoints;

/// <summary>
/// 语音转写端点：接收浏览器录音，转成文字返回。
/// </summary>
/// <remarks>
/// 与 <see cref="UploadEndpoints"/> 同构（会话校验 + 自定义头防空表单），
/// 差别是这里不落盘——音频只进内存，转写完即释放，
/// 因此不需要清理临时文件的生命周期管理。
/// </remarks>
public static class TranscriptionEndpoints
{
    /// <summary>转写路由。</summary>
    public const string Route = "/api/transcriptions";

    /// <summary>映射转写端点。</summary>
    public static IEndpointRouteBuilder MapTranscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(Route, HandleAsync)
            .WithMetadata(new RequestSizeLimitAttribute(VoiceInputLayout.MaxAudioBytes + (1024 * 1024)));
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        string sessionId,
        SpeechTranscriptionService service,
        ISessionManager sessions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessions.Get(sessionId) is null)
            return Results.NotFound(new { error = "session not found" });

        if (!request.Headers.TryGetValue(VoiceInputLayout.RequestHeaderName, out var voiceHeader)
            || !string.Equals(voiceHeader.ToString(), VoiceInputLayout.RequestHeaderValue, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "invalid voice request", code = "invalidRequest" });

        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "audio required", code = "invalidRequest" });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.FirstOrDefault();
        if (file is null || file.Length <= 0 || file.Length > VoiceInputLayout.MaxAudioBytes)
            return Results.BadRequest(new { error = "invalid audio size", code = "invalidRequest" });

        var mimeType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType;
        var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "voice.webm" : file.FileName;

        // 读进 MemoryStream：重试装饰器需要 Seek 回起点，请求体流不可 seek。
        using var buffer = new MemoryStream((int)file.Length);
        await using (var source = file.OpenReadStream())
        {
            await source.CopyToAsync(buffer, ct);
        }

        buffer.Position = 0;

        try
        {
            var text = await service.TranscribeAsync(sessionId, buffer, mimeType, fileName, ct);
            var segments = Array.Empty<object>();
            return Results.Json(new { text, segments });
        }
        catch (SpeechTranscriptionUnavailableException ex)
        {
            return Results.Json(
                new { error = ex.Reason, code = "modelNotConfigured" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory.CreateLogger(typeof(TranscriptionEndpoints))
                .LogError(ex, "语音转写失败: Session={SessionId}, Mime={MimeType}", sessionId, mimeType);

            return Results.Json(
                new { error = ex.Message, code = "transcriptionFailed" },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
