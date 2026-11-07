using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Seeing.Agent.Abstractions.Execution;
using Seeing.Agent.Abstractions.Llm;
using Seeing.Agent.Abstractions.Permissions;
using Seeing.Agent.Abstractions.Tools;
using Seeing.Agent.Llm;
using Seeing.Agent.Tools.Support;
using Seeing.Agent.Tools.Support.Llm;
using Seeing.Agent.Tools.Support.Speech;
using System.Text.Json;

namespace Seeing.Agent.Tools.Speech;

/// <summary>
/// 音频转写工具：把工作区里的音频文件转成文本，交给模型继续分析。
/// </summary>
/// <remarks>
/// <para>
/// 走 <see cref="ILlmInvoker"/> 而不是直连 <see cref="ILlmRouter"/>：转写与 chat 同属一次
/// LLM 调用，应当触发 <c>llm.*</c> Hook、合并出站头、计入用量统计。
/// </para>
/// <para>
/// <b>为什么必须读进 <see cref="MemoryStream"/></b>：重试装饰器在重试前会把
/// <see cref="TranscriptionRequest.Audio"/> 定位回起点，要求流可 seek。
/// </para>
/// </remarks>
[ToolCapability(ToolCapabilityKeys.TimeoutBudget, "300000")]
public sealed class TranscribeAudioTool : BuiltInToolBase
{
    /// <summary>未配置语音转写默认模型时的可读原因（与 WebUI 同口径）。</summary>
    internal const string NoTranscriptionModelMessage =
        "未配置语音转写的默认模型：请在模型页的「默认模型（按能力）」区块为 " +
        "speechTranscription 选择模型，或在调用时显式传入 model。";

    private readonly IFileSystem _fileSystem;
    private readonly IWorkspacePathGate _pathGate;

    /// <summary>创建转写工具实例。</summary>
    public TranscribeAudioTool(
        ILogger<TranscribeAudioTool> logger,
        IFileSystem fileSystem,
        IWorkspacePathGate pathGate) : base(logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _pathGate = pathGate ?? throw new ArgumentNullException(nameof(pathGate));
    }

    /// <inheritdoc />
    public override string Id => "transcribe_audio";

    /// <inheritdoc />
    public override string Description =>
        "把音频文件转写为文字，用于分析录音内容。\n\n" +
        "- 文件：支持 mp3、wav、m4a、mp4、ogg、opus、webm、flac、aac、amr、wma、silk；单文件上限 25MB。\n" +
        "- 模型：默认用 speechTranscription 的默认模型；省略 model 且未配置时直接报错，不会自动挑模型。\n" +
        "- 输出：转写全文；服务端提供分段时附带时间区间，未提供时注明。\n" +
        "- 前置：需要已配置可用的语音转写模型；不转换音频格式（MiMo 仅收 mp3/wav）。\n" +
        "- 不用于：读取文本文件（用 `read`）；音频内容的音色与情绪无法保留，只得到文字。";

    /// <inheritdoc />
    public override ToolCategory Category => ToolCategory.LlmInteraction;

    /// <inheritdoc />
    public override JsonElement ParametersSchema => BuildParametersSchema();

    private static JsonElement BuildParametersSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "要转写的音频文件：绝对路径，或相对当前工作目录的路径"
                },
                language = new
                {
                    type = "string",
                    description = "可选：语言提示（如 zh、en）；省略时由服务端自动判定"
                },
                model = new
                {
                    type = "string",
                    description = "可选：模型引用（provider/model）；省略时使用 DefaultModels.speechTranscription"
                }
            },
            required = new[] { "path" }
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    /// <inheritdoc />
    public override async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
    {
        var filePath = GetStringArgument(arguments, "path");
        if (string.IsNullOrWhiteSpace(filePath))
            return Failure("缺少必需参数: path");

        if (!Path.IsPathRooted(filePath))
            filePath = _fileSystem.GetFullPath(filePath);
        else
            filePath = ResolvePath(filePath);

        var denied = _pathGate.EnsureAllowed(context.SessionId ?? string.Empty, filePath);
        if (denied is not null)
            return Failure(denied);

        if (!_fileSystem.Exists(filePath) || _fileSystem.IsDirectory(filePath))
            return Failure($"文件不存在: {filePath}");

        if (!AudioFormatSupport.TryGetMimeType(filePath, out var mimeType))
        {
            return Failure(
                $"不是支持的音频格式: {Path.GetExtension(filePath)}。" +
                $"支持的扩展名：{string.Join("、", AudioFormatSupport.SupportedExtensions)}");
        }

        long size;
        try
        {
            size = _fileSystem.GetFileLength(filePath);
        }
        catch (IOException ex)
        {
            return Failure($"无法读取文件大小: {ex.Message}");
        }

        if (size <= 0)
            return Failure($"音频文件为空: {filePath}");

        if (size > AudioFormatSupport.MaxAudioBytes)
        {
            return Failure(
                $"音频文件超过上限：{TranscribeOutputFormatter.FormatSize(size)} > " +
                $"{TranscribeOutputFormatter.FormatSize(AudioFormatSupport.MaxAudioBytes)}");
        }

        var services = context.Services;
        var models = services?.GetService<IModelConfigManager>();
        var invoker = services?.GetService<ILlmInvoker>();
        if (models is null || invoker is null)
            return Failure("语音转写能力未接入（请启用 llm 与 tools.speech 模块）");

        var modelRef = GetStringArgument(arguments, "model");
        if (string.IsNullOrWhiteSpace(modelRef))
            modelRef = models.GetDefaultModel(LlmCapabilityKind.SpeechTranscription);

        if (string.IsNullOrWhiteSpace(modelRef))
            return Failure(NoTranscriptionModelMessage);

        var language = GetStringArgument(arguments, "language");
        var fileName = Path.GetFileName(filePath);

        try
        {
            var bytes = await _fileSystem.ReadAllBytesAsync(filePath, context.CancellationToken)
                .ConfigureAwait(false);

            // MemoryStream 天然可 seek：重试装饰器会把它定位回起点。
            using var buffer = new MemoryStream(bytes, writable: false);
            var wireModel = ModelReference.ToWireModelId(models, modelRef);

            _logger.LogDebug(
                "语音转写开始: Session={SessionId}, Model={Model}, Size={Size}, Mime={Mime}",
                context.SessionId, modelRef, size, mimeType);

            var result = await invoker.InvokeAsync<ISpeechTranscriptionClient, TranscriptionResult>(
                modelRef,
                (client, call, ct) => client.TranscribeAsync(
                    new TranscriptionRequest
                    {
                        Model = wireModel,
                        Audio = buffer,
                        MimeType = mimeType,
                        FileName = fileName,
                        Language = language,
                    },
                    call,
                    ct),
                options: new LlmInvocationOptions { SessionId = context.SessionId },
                cancellationToken: context.CancellationToken).ConfigureAwait(false);

            var segmentCount = result.Segments?.Count ?? 0;
            var output = TranscribeOutputFormatter.Format(fileName, size, mimeType, modelRef, result);

            _logger.LogDebug(
                "语音转写完成: Session={SessionId}, Model={Model}, Chars={Chars}, Segments={Segments}",
                context.SessionId, modelRef, result.Text?.Length ?? 0, segmentCount);

            return Success("语音转写", output, new Dictionary<string, object>
            {
                ["path"] = filePath,
                ["mimeType"] = mimeType,
                ["size"] = size,
                ["model"] = modelRef,
                ["segmentCount"] = segmentCount,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LlmCapabilityMismatchException ex)
        {
            return Failure($"语音转写路由失败: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return Failure($"当前模型不支持该音频格式: {ex.Message}");
        }
        catch (Exception ex)
        {
            // 兜底文案必须带 ex.Message：HTTP 状态码与服务端错误体是唯一能定位问题的线索，
            // 只回「语音转写失败」会让用户无从判断是格式、鉴权还是服务端问题。
            return Failure(ex, $"语音转写失败：{ex.Message}");
        }
    }
}
