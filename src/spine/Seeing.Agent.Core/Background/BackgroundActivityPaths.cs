using Seeing.Agent.Configuration;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台活动落盘路径与 id 校验的唯一实现（快照与输出共用）。
/// <para>
/// 两处写盘必须解出同一个活动根：各自算一遍会在「工作区未决议 → 用户级目录」这类
/// 回落分支上漂移，导致快照与输出落在不同目录。
/// </para>
/// </summary>
internal static class BackgroundActivityPaths
{
    /// <summary>活动根目录名（位于项目 <c>.seeing</c> 或用户 <c>~/.seeing</c> 下）。</summary>
    internal const string DirectoryName = "background-activities";

    /// <summary>快照文件后缀。</summary>
    internal const string SnapshotFileSuffix = ".json";

    /// <summary>输出文件后缀。</summary>
    internal const string OutputFileSuffix = ".output.log";

    private const string ActivityIdPrefix = "act_";
    private const int ActivityIdHexLength = 32;

    /// <summary>
    /// 活动根：工作区已决议 → <c>{项目根}/.seeing/background-activities</c>；
    /// 否则 <c>~/.seeing/background-activities</c>。
    /// </summary>
    internal static string ResolveActivityRoot(IWorkspaceProvider workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            var projectRoot = workspace.GetProjectRoot();
            if (!string.IsNullOrWhiteSpace(projectRoot))
                return Path.Combine(projectRoot, ".seeing", DirectoryName);
        }
        catch (InvalidOperationException)
        {
            // 工作区尚未决议：占位目录绝不能当工作区，回落到用户级目录（spec §11.1）。
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".seeing", DirectoryName);
    }

    /// <summary>活动 id 形如 <c>act_</c> + 32 位小写十六进制（与 manager 的 <c>NewActivityId</c> 对齐）。</summary>
    internal static bool IsValidActivityId(string? activityId)
    {
        if (activityId is null || activityId.Length != ActivityIdPrefix.Length + ActivityIdHexLength)
            return false;

        if (!activityId.StartsWith(ActivityIdPrefix, StringComparison.Ordinal))
            return false;

        for (var i = ActivityIdPrefix.Length; i < activityId.Length; i++)
        {
            var c = activityId[i];
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
                return false;
        }

        return true;
    }
}
