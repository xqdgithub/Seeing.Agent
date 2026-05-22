using System;

namespace Seeing.Agent.MCP.OAuth
{
    /// <summary>
    /// OAuth 令牌
    /// </summary>
    public class McpOAuthToken
    {
        /// <summary>访问令牌</summary>
        public string AccessToken { get; set; } = "";

        /// <summary>刷新令牌</summary>
        public string? RefreshToken { get; set; }

        /// <summary>令牌类型</summary>
        public string TokenType { get; set; } = "Bearer";

        /// <summary>过期时间（秒）</summary>
        public int ExpiresIn { get; set; }

        /// <summary>授权范围</summary>
        public string? Scope { get; set; }

        /// <summary>创建时间</summary>
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>过期时间</summary>
        public DateTimeOffset ExpiresAt => CreatedAt.AddSeconds(ExpiresIn);

        /// <summary>是否已过期</summary>
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

        /// <summary>是否即将过期（5分钟内）</summary>
        public bool IsExpiringSoon => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-5);
    }
}
