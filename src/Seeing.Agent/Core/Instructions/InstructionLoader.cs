using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Core.Instructions;

/// <summary>
/// 指令加载器实现
/// 从 AGENTS.md 文件动态加载 Agent 指令
/// </summary>
public class InstructionLoader : IInstructionLoader
{
    private const string InstructionFileName = "AGENTS.md";
    private readonly ILogger<InstructionLoader> _logger;

    /// <summary>
    /// 创建指令加载器实例
    /// </summary>
    public InstructionLoader(ILogger<InstructionLoader> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InstructionFile?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            _logger.LogDebug("指令文件不存在: {Path}", path);
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            var lastModified = File.GetLastWriteTimeUtc(path);

            _logger.LogInformation("成功加载指令文件: {Path}, 内容长度: {Length}", path, content.Length);

            return new InstructionFile
            {
                Path = path,
                Content = content,
                LastModified = lastModified
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载指令文件失败: {Path}", path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstructionFile>> DiscoverAsync(
        string? baseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<InstructionFile>();
        var searchDirectory = baseDirectory ?? Directory.GetCurrentDirectory();

        // 1. 当前目录 ./AGENTS.md
        var currentPath = Path.Combine(searchDirectory, InstructionFileName);
        var currentFile = await LoadAsync(currentPath, cancellationToken);
        if (currentFile != null)
        {
            results.Add(currentFile);
            _logger.LogDebug("发现当前目录指令文件: {Path}", currentPath);
        }

        // 2. 父目录 ../AGENTS.md
        var parentDirectory = Directory.GetParent(searchDirectory);
        if (parentDirectory != null)
        {
            var parentPath = Path.Combine(parentDirectory.FullName, InstructionFileName);
            var parentFile = await LoadAsync(parentPath, cancellationToken);
            if (parentFile != null)
            {
                results.Add(parentFile);
                _logger.LogDebug("发现父目录指令文件: {Path}", parentPath);
            }
        }

        // 3. 用户主目录 ~/.agents/AGENTS.md
        var userHomePath = GetUserAgentsPath();
        if (userHomePath != null)
        {
            var userFile = await LoadAsync(userHomePath, cancellationToken);
            if (userFile != null)
            {
                results.Add(userFile);
                _logger.LogDebug("发现用户主目录指令文件: {Path}", userHomePath);
            }
        }

        _logger.LogInformation("发现 {Count} 个指令文件", results.Count);
        return results;
    }

    /// <inheritdoc />
    public string Merge(IEnumerable<InstructionFile> files)
    {
        var fileList = files.ToList();
        if (fileList.Count == 0)
        {
            return string.Empty;
        }

        if (fileList.Count == 1)
        {
            return fileList[0].Content;
        }

        // 合并多个文件，按搜索顺序添加分隔符
        var mergedContent = new System.Text.StringBuilder();
        for (var i = 0; i < fileList.Count; i++)
        {
            if (i > 0)
            {
                mergedContent.AppendLine();
                mergedContent.AppendLine("---");
                mergedContent.AppendLine();
            }

            // 添加来源信息注释
            mergedContent.AppendLine($"<!-- 来源: {fileList[i].Path} -->");
            mergedContent.AppendLine();
            mergedContent.Append(fileList[i].Content);
        }

        _logger.LogDebug("合并 {Count} 个指令文件，总长度: {Length}", fileList.Count, mergedContent.Length);
        return mergedContent.ToString();
    }

    /// <summary>
    /// 获取用户主目录下的 .agents/AGENTS.md 路径
    /// </summary>
    private static string? GetUserAgentsPath()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userHome))
        {
            return null;
        }

        return Path.Combine(userHome, ".agents", InstructionFileName);
    }
}