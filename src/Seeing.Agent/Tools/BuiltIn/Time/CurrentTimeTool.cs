using Microsoft.Extensions.Logging;
using Seeing.Agent.Core.Abstractions;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Core.Models;
using System.Text.Json;

namespace Seeing.Agent.Tools.BuiltIn.Time
{
    /// <summary>
    /// 当前时间工具 - 获取本地或指定时区的当前时间（ISO 8601）
    /// </summary>
    public class CurrentTimeTool : ToolBase
    {
        /// <summary>
        /// 创建 CurrentTimeTool 实例
        /// </summary>
        public CurrentTimeTool(ILogger<CurrentTimeTool> logger) : base(logger)
        {
        }

        /// <inheritdoc/>
        public override string Id => "current_time";

        /// <inheritdoc/>
        public override string Description =>
            "获取当前时间。可选指定 IANA 时区（如 Asia/Shanghai、UTC、America/New_York）；" +
            "省略则使用本机本地时区。返回 ISO 8601 格式（含偏移）。";

        /// <inheritdoc/>
        public override JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                timezone = new
                {
                    type = "string",
                    description = "IANA 时区 ID，例如 Asia/Shanghai、UTC、America/New_York；省略则使用本机本地时区"
                }
            }
        });

        /// <inheritdoc/>
        public override Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext context)
        {
            var timezoneId = GetStringArgument(arguments, "timezone");

            TimeZoneInfo timeZone;
            if (string.IsNullOrWhiteSpace(timezoneId))
            {
                timeZone = TimeZoneInfo.Local;
            }
            else if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezoneId, out var resolved))
            {
                return Task.FromResult(Failure(
                    $"未知时区: {timezoneId}。请使用 IANA 时区 ID，例如 Asia/Shanghai"));
            }
            else
            {
                timeZone = resolved;
            }

            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
            return Task.FromResult(Success(localNow.ToString("o")));
        }
    }
}
