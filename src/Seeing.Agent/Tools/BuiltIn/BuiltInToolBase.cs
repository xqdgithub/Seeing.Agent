using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using Seeing.Agent.Middlewares;
using Seeing.Agent.Tools.BuiltIn.FileSystem;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn
{
    /// <summary>
    /// 内置工具基类 - 提供内置工具的通用功能
    /// </summary>
    public abstract class BuiltInToolBase : ToolBase
    {
        protected readonly string _workingDirectory;

        protected BuiltInToolBase(ILogger logger, string? workingDirectory = null)
            : base(logger)
        {
            _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        }

        /// <summary>
        /// 工具标签
        /// </summary>
        public virtual IReadOnlyList<string> Tags => new[] { "built-in" };

        /// <summary>
        /// 解析文件路径（支持相对路径转绝对路径）
        /// </summary>
        protected string ResolvePath(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return _workingDirectory;

            if (Path.IsPathRooted(filePath))
                return filePath;

            return Path.GetFullPath(Path.Combine(_workingDirectory, filePath));
        }

        /// <summary>
        /// 检查路径是否在工作目录内
        /// </summary>
        protected bool IsWithinWorkingDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var fullWorkingDir = Path.GetFullPath(_workingDirectory);
            return fullPath.StartsWith(fullWorkingDir, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取相对路径
        /// </summary>
        protected string GetRelativePath(string absolutePath)
        {
            return Path.GetRelativePath(_workingDirectory, absolutePath);
        }

        /// <summary>
        /// 请求权限确认
        /// </summary>
        protected async Task<bool> AskPermissionAsync(
            ToolContext context,
            string permission,
            List<string> patterns,
            Dictionary<string, object>? metadata = null)
        {
            if (context.AskPermission == null)
                return true;

            try
            {
                await context.AskPermission(new PermissionRequest
                {
                    Permission = permission,
                    Patterns = patterns,
                    Metadata = metadata ?? new Dictionary<string, object>()
                });
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// 构建标准 JSON Schema
        /// </summary>
        protected JsonElement BuildSchema(Dictionary<string, object> schema)
        {
            return JsonSerializer.SerializeToElement(schema);
        }

        /// <summary>
        /// 构建带属性的对象 Schema
        /// </summary>
        protected JsonElement BuildObjectSchema(
            Dictionary<string, (string Type, string Description, bool Required, string[]? EnumValues)> properties)
        {
            var props = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var kvp in properties)
            {
                var prop = new Dictionary<string, object>
                {
                    ["type"] = kvp.Value.Type,
                    ["description"] = kvp.Value.Description
                };

                if (kvp.Value.EnumValues != null && kvp.Value.EnumValues.Length > 0)
                {
                    prop["enum"] = kvp.Value.EnumValues;
                }

                props[kvp.Key] = prop;

                if (kvp.Value.Required)
                {
                    required.Add(kvp.Key);
                }
            }

            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = props
            };

            if (required.Count > 0)
            {
                schema["required"] = required.ToArray();
            }

            return JsonSerializer.SerializeToElement(schema);
        }

        private static readonly ConcurrentDictionary<string, HashSet<string>> s_approvedPathsCache = new(StringComparer.OrdinalIgnoreCase);
        private const int s_maxCacheEntries = 256;

        /// <summary>
        /// 检查文件路径是否在工作区内，外部路径申请权限（同会话缓存批准结果）
        /// </summary>
        protected async Task<ToolResult?> CheckPathWithinWorkspaceAsync(string filePath, ToolContext context)
        {
            var workspace = context.Services?.GetService<IWorkspaceProvider>();
            if (workspace == null) return null;

            if (FileSystemHelper.IsPathWithinDirectory(filePath, workspace.WorkspaceRoot))
                return null;

            if (s_approvedPathsCache.TryGetValue(context.SessionId, out var approved))
            {
                lock (approved)
                {
                    if (approved.Contains(filePath))
                        return null;
                }
            }

            if (context.AskPermission != null)
            {
                try
                {
                    await context.AskPermission(new PermissionRequest
                    {
                        Permission = "filesystem.external",
                        Patterns = new List<string> { filePath },
                        Metadata = new Dictionary<string, object>
                        {
                            ["path"] = filePath,
                            ["workspace"] = workspace.WorkspaceRoot,
                            ["reason"] = "访问工作区外的路径"
                        }
                    });

                    var sessionApproved = s_approvedPathsCache.GetOrAdd(context.SessionId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    lock (sessionApproved)
                    {
                        sessionApproved.Add(filePath);
                    }

                    PurgeCacheIfNeeded();
                    return null;
                }
                catch (PermissionDeniedException)
                {
                    return Failure("安全限制：拒绝访问工作区外的路径。如需访问，请在权限配置中允许 filesystem.external。");
                }
            }

            return Failure("安全限制：不允许访问工作区外的路径。");
        }

        private void PurgeCacheIfNeeded()
        {
            if (s_approvedPathsCache.Count <= s_maxCacheEntries) return;

            var toRemove = s_approvedPathsCache.Keys.Take(s_approvedPathsCache.Count - s_maxCacheEntries / 2).ToList();
            foreach (var key in toRemove)
            {
                s_approvedPathsCache.TryRemove(key, out _);
            }
        }
    }
}