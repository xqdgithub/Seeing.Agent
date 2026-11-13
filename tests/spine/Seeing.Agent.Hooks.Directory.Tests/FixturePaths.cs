namespace Seeing.Agent.Hooks.Directory.Tests;

public static class FixturePaths
{
    public static string HandlerDll() => Required("Seeing.Agent.Hooks.Directory.Fixtures");

    public static string EmptyDll() => Required("Seeing.Agent.Hooks.Directory.EmptyFixture");

    private static string Required(string project)
    {
        var baseDir = AppContext.BaseDirectory;
        // BaseDirectory 带尾部分隔符。Directory.GetParent 会停在 net10.0 自身，要用 DirectoryInfo.Parent。
        var configuration = new DirectoryInfo(baseDir).Parent?.Name
            ?? throw new FileNotFoundException("输出目录没有 Configuration 一级", baseDir);
        var spine = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var path = Path.Combine(spine, project, "bin", configuration, "net10.0", project + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException("夹具程序集不存在: " + path, path);
        return path;
    }
}
