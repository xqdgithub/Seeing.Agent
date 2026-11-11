using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Output;
using Seeing.Agent.Paths;
using Seeing.Agent.Tools.Support;
using Seeing.Agent.WebUI.Endpoints;

namespace Seeing.Agent.WebUI.Services;

/// <summary>
/// 会话内本地路径 → 浏览器可加载 URL 的**唯一生成点**。
/// <para>
/// 组件不得各自拼 URL 字符串：路径归属（ref / uploads）、URL 转义、越界判定只应有一处实现。
/// </para>
/// <para>
/// 两个来源：
/// <list type="bullet">
/// <item><b>ref</b>：工具产出（截图、二进制读取）落在 <c>{sessionId}.ref/</c>。</item>
/// <item><b>uploads</b>：用户粘贴/拖拽的附件落在 <c>{sessionRoot}/.seeing/uploads/{sessionId}/</c>。</item>
/// </list>
/// 都不匹配（跨机、历史遗留、外部绝对路径）时返回 <see langword="null"/>，调用方回退到 base64 或占位。
/// </para>
/// </summary>
public sealed class SessionAssetUrlBuilder
{
    private readonly IToolOutputStore _outputStore;
    private readonly IWorkspaceProvider _project;

    /// <summary>创建 URL 生成器。</summary>
    public SessionAssetUrlBuilder(IToolOutputStore outputStore, IWorkspaceProvider project)
    {
        _outputStore = outputStore ?? throw new ArgumentNullException(nameof(outputStore));
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <summary>
    /// 把会话内本地路径转成资源 URL；不属于该会话目录时返回 <see langword="null"/>。
    /// </summary>
    /// <param name="sessionId">会话 id。</param>
    /// <param name="localPath">会话里保存的绝对路径。</param>
    public string? ForSessionPath(string? sessionId, string? localPath)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(localPath))
            return null;

        // 工具产出：ref 目录
        var refDir = SafeGetRefDirectory(sessionId);
        if (refDir is not null && PathBoundary.IsStrictlyWithin(localPath, refDir))
        {
            var relative = Path.GetRelativePath(refDir, localPath);
            return BuildUrl(sessionId, SessionAssetEndpoints.ScopeRef, relative);
        }

        // 用户附件：uploads 目录
        var projectRoot = _project.GetProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var uploadsDir = PastedFileLayout.BuildSessionDirectory(projectRoot, sessionId);
            if (PathBoundary.IsStrictlyWithin(localPath, uploadsDir))
            {
                var relative = Path.GetRelativePath(uploadsDir, localPath);
                return BuildUrl(sessionId, SessionAssetEndpoints.ScopeUploads, relative);
            }
        }

        return null;
    }

    private static string BuildUrl(string sessionId, string scope, string relativePath)
    {
        var escapedSession = Uri.EscapeDataString(sessionId);
        var escapedPath = string.Join(
            "/",
            relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        return $"/api/sessions/{escapedSession}/assets/{scope}/{escapedPath}";
    }

    private string? SafeGetRefDirectory(string sessionId)
    {
        try
        {
            return _outputStore.GetRefDirectory(sessionId);
        }
        catch
        {
            return null;
        }
    }
}
