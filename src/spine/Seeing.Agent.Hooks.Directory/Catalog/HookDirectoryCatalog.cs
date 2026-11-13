using System.Globalization;
using System.Text.RegularExpressions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Paths;

namespace Seeing.Agent.Hooks.Directory.Catalog;

public enum HookFileDisposition
{
    Script,
    EntryDll,
    Ignored,
}

public sealed record HookSnapshotFile(
    string FullPath,
    string RelativePath,
    HookFileDisposition Disposition,
    string? Point,
    int? ExplicitPriority,
    string? ContentHash);

public sealed record ScriptBinding(
    string FullPath,
    string RelativePath,
    string Point,
    int Priority,
    HookSpec Spec);

public sealed record DllBinding(
    string FullPath,
    string? ContentHash);

public sealed record HookCatalogDiff(
    IReadOnlyList<ScriptBinding> AddScripts,
    IReadOnlyList<string> RemoveScriptPaths,
    IReadOnlyList<DllBinding> AddDlls,
    IReadOnlyList<string> RemoveDllPaths);

public static class HookDirectoryCatalog
{
    private static readonly Regex PriorityLine = new(
        @"^// @priority (-?\d+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string PathKey(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static HookSnapshotFile Classify(
        string hooksRoot,
        string fullPath,
        bool withinBoundary,
        string? firstLine,
        string? contentHash)
    {
        var relative = Path.GetRelativePath(hooksRoot, fullPath).Replace('\\', '/');
        if (!withinBoundary || HasSkippedSegment(relative) || !IsHookExtension(fullPath))
            return Ignored(fullPath, relative);

        var segments = relative.Split('/');
        if (IsJs(fullPath))
        {
            if (segments.Length is not (1 or 2))
                return Ignored(fullPath, relative);

            var point = segments.Length == 1
                ? Path.GetFileNameWithoutExtension(segments[0])
                : segments[0];
            if (HookPointNames.IsLegacy(point))
                return Ignored(fullPath, relative);

            return new HookSnapshotFile(
                fullPath,
                relative,
                HookFileDisposition.Script,
                point,
                ParsePriority(firstLine),
                null);
        }

        if (segments.Length != 1)
            return Ignored(fullPath, relative);

        return new HookSnapshotFile(
            fullPath,
            relative,
            HookFileDisposition.EntryDll,
            null,
            null,
            contentHash);
    }

    public static HookCatalogDiff Diff(
        IReadOnlyList<ScriptBinding> currentScripts,
        IReadOnlyList<DllBinding> currentDlls,
        IReadOnlyList<HookSnapshotFile> snapshot)
    {
        var comparer = StringComparer.FromComparison(PathBoundary.PathComparison);
        var (removeScripts, addScripts) = DiffByKey(
            currentScripts,
            DesiredScripts(snapshot),
            script => PathKey(script.FullPath),
            SameScript,
            comparer);
        var (removeDlls, addDlls) = DiffByKey(
            currentDlls,
            DesiredDlls(snapshot),
            dll => PathKey(dll.FullPath),
            SameDll,
            comparer);

        return new HookCatalogDiff(addScripts, removeScripts, addDlls, removeDlls);
    }

    private static bool SameScript(ScriptBinding current, ScriptBinding desired)
        => string.Equals(current.Point, desired.Point, StringComparison.Ordinal)
           && current.Priority == desired.Priority;

    private static bool SameDll(DllBinding current, DllBinding desired)
        => string.Equals(current.ContentHash, desired.ContentHash, StringComparison.Ordinal);

    private static List<ScriptBinding> DesiredScripts(IReadOnlyList<HookSnapshotFile> snapshot)
    {
        var scripts = snapshot
            .Where(file => file.Disposition == HookFileDisposition.Script && file.Point is not null)
            .ToList();
        var result = new List<ScriptBinding>(scripts.Count);
        foreach (var group in scripts.GroupBy(file => file.Point!, StringComparer.Ordinal))
        {
            var autos = group
                .Where(file => file.ExplicitPriority is null)
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
            var autoPriority = new Dictionary<HookSnapshotFile, int>();
            for (var i = 0; i < autos.Count; i++)
                autoPriority[autos[i]] = -1000 + i;

            foreach (var file in group)
            {
                var priority = file.ExplicitPriority ?? autoPriority[file];
                result.Add(new ScriptBinding(
                    file.FullPath,
                    file.RelativePath,
                    file.Point!,
                    priority,
                    HookPointNames.Resolve(file.Point!)));
            }
        }

        return result;
    }

    private static List<DllBinding> DesiredDlls(IReadOnlyList<HookSnapshotFile> snapshot)
        => snapshot
            .Where(file => file.Disposition == HookFileDisposition.EntryDll)
            .Select(file => new DllBinding(file.FullPath, file.ContentHash))
            .ToList();

    private static (List<string> Remove, List<T> Add) DiffByKey<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> desired,
        Func<T, string> keyOf,
        Func<T, T, bool> same,
        StringComparer comparer)
    {
        var currentMap = new Dictionary<string, T>(comparer);
        foreach (var item in current)
            currentMap[keyOf(item)] = item;

        var desiredMap = new Dictionary<string, T>(comparer);
        foreach (var item in desired)
            desiredMap[keyOf(item)] = item;

        var remove = new List<string>();
        foreach (var (key, item) in currentMap)
        {
            if (!desiredMap.TryGetValue(key, out var next) || !same(item, next))
                remove.Add(key);
        }

        var add = new List<T>();
        foreach (var (key, item) in desiredMap)
        {
            if (!currentMap.TryGetValue(key, out var previous) || !same(previous, item))
                add.Add(item);
        }

        return (remove, add);
    }

    private static bool HasSkippedSegment(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0)
                continue;
            if (segment[0] == '.'
                || segment.Equals(".cache", StringComparison.Ordinal)
                || segment.Equals("node_modules", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsHookExtension(string fullPath) => IsJs(fullPath) || IsDll(fullPath);

    private static bool IsJs(string fullPath)
        => Path.GetExtension(fullPath).Equals(".js", StringComparison.OrdinalIgnoreCase);

    private static bool IsDll(string fullPath)
        => Path.GetExtension(fullPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);

    private static int? ParsePriority(string? firstLine)
    {
        if (string.IsNullOrEmpty(firstLine))
            return null;

        var match = PriorityLine.Match(firstLine.Trim());
        if (!match.Success)
            return null;

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static HookSnapshotFile Ignored(string fullPath, string relative)
        => new(fullPath, relative, HookFileDisposition.Ignored, null, null, null);
}
