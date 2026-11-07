using Seeing.Agent.Paths;

namespace Seeing.Agent.Core.Permission;

/// <summary>
/// 路径边界检查 — 委托给唯一实现 <see cref="PathBoundary"/>。
/// <para>
/// 保留本类仅为不破坏 Core 内部调用点；<b>不要再往这里加逻辑</b>：
/// 任何边界语义变更都必须落到 <see cref="PathBoundary"/>，否则会重新出现
/// 「Core / 文件工具 / Skills / ACP 各有一份、符号链接语义不一致」的分叉
/// （历史缺陷：ACP 与 Skills 的副本不做 realpath，符号链接可写出工作区）。
/// </para>
/// </summary>
internal static class PathSafety
{
    /// <summary>
    /// 判断 <paramref name="path"/> 是否位于 <paramref name="baseDirectory"/> 之内（含相等）。
    /// </summary>
    public static bool IsPathWithinDirectory(string path, string baseDirectory)
        => PathBoundary.IsWithin(path, baseDirectory);
}
