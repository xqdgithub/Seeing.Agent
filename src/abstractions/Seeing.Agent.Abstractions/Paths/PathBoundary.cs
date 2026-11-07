namespace Seeing.Agent.Paths;

/// <summary>
/// 路径边界判定 — 全项目唯一实现。
/// <para>
/// <b>为什么必须是唯一实现：</b>「<paramref name="path"/> 是否位于 <paramref name="baseDirectory"/> 之内」
/// 是硬边界（<c>Workspace.RestrictToWorkspace</c>）与权限判定的公共底座。此前它在 5 处各自实现
/// （Core 的 <c>PathSafety</c>、Tools.FileSystem 的 <c>FileSystemHelper</c>、Skills 的两处内联前缀比较、
/// ACP 文件桥的字符串前缀比较），其中 <b>ACP 与 Skills 的副本不做符号链接真实路径解析</b>——
/// 于是 <c>root/evil -&gt; C:\Windows</c> 这类符号链接可以写出工作区，安全语义已经分叉。
/// </para>
/// <para>
/// <b>为什么放在契约层：</b>它是无状态纯函数（与 <c>ToolResultFormatting</c>、<c>ModelCapabilityFillEmpty</c>
/// 同属既有的「Abstractions 纯函数豁免」清单）；所有调用方（Core / 各能力包 / Gateway）都已引用
/// Abstractions，因此无需新增依赖边。
/// </para>
/// <para><b>语义要点：</b>
/// <list type="bullet">
/// <item>目标存在时解析符号链接到最终目标；目标不存在时递归解析父目录，尾段原样拼回。</item>
/// <item>比较必须带分隔符边界，避免 <c>E:\ws\apple</c> 命中 <c>root=E:\ws\app</c>。</item>
/// <item>Windows / macOS 大小写不敏感，Linux 敏感。</item>
/// <item>任何异常（非法路径、权限不足）一律判为「不在边界内」（fail-closed）。</item>
/// </list>
/// </para>
/// </summary>
public static class PathBoundary
{
    /// <summary>按当前平台取路径比较语义（Windows / macOS 不敏感，其余敏感）。</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// 判断 <paramref name="path"/> 是否位于 <paramref name="baseDirectory"/> 之内（含相等）。
    /// <para>两侧都会先绝对化并解析符号链接。任一参数为空或非法路径时返回 <see langword="false"/>。</para>
    /// </summary>
    /// <param name="path">待判定的路径（相对路径按进程当前目录解析）。</param>
    /// <param name="baseDirectory">边界根目录。</param>
    public static bool IsWithin(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(baseDirectory))
            return false;

        try
        {
            var fullPath = ResolveRealPath(Path.GetFullPath(path));
            var fullBase = ResolveRealPath(Path.GetFullPath(baseDirectory));

            if (string.Equals(fullPath, fullBase, PathComparison))
                return true;

            // 带分隔符前缀：避免 E:\ws\apple 误匹配 E:\ws\app
            var prefix = EndsWithSeparator(fullBase)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(prefix, PathComparison);
        }
        catch
        {
            // fail-closed：无法判定边界时按「越界」处理
            return false;
        }
    }

    /// <summary>
    /// 判断 <paramref name="path"/> 是否<b>严格</b>位于 <paramref name="baseDirectory"/> 之内（不含相等）。
    /// </summary>
    public static bool IsStrictlyWithin(string path, string baseDirectory)
        => IsWithin(path, baseDirectory) && !AreSame(path, baseDirectory);

    /// <summary>
    /// 判断两个路径是否指向同一位置（忽略尾部分隔符，按平台大小写语义）。
    /// 任一非法路径或解析异常返回 <see langword="false"/>。
    /// </summary>
    public static bool AreSame(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            var normalizedLeft = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(normalizedLeft, normalizedRight, PathComparison);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析符号链接的真实路径；非链接、不存在或解析失败时返回（父目录已解析后的）原路径。
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                var info = new DirectoryInfo(fullPath);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName ?? info.FullName;
            }

            if (File.Exists(fullPath))
            {
                var info = new FileInfo(fullPath);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName ?? info.FullName;
            }

            // 目标不存在：解析父目录，尾段原样拼回（保持已存在部分的真实路径）
            var parent = Directory.GetParent(fullPath);
            if (parent is null)
                return fullPath;

            var resolvedParent = ResolveRealPath(parent.FullName);
            return Path.Combine(resolvedParent, Path.GetFileName(fullPath));
        }
        catch
        {
            return fullPath;
        }
    }

    private static bool EndsWithSeparator(string value) =>
        value.EndsWith(Path.DirectorySeparatorChar) || value.EndsWith(Path.AltDirectorySeparatorChar);
}
