using Seeing.Agent.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Models;
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
        public override IReadOnlyList<string> Tags => new[] { "built-in" };

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

    }
}