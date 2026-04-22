using System;
using System.Collections.Generic;
using Seeing.Session.Core;

namespace Seeing.Session.Hooks
{
    /// <summary>
    /// Session Hook 上下文
    /// </summary>
    public class SessionHookContext
    {
        /// <summary>Hook 点</summary>
        public string HookPoint { get; set; } = string.Empty;
        
        /// <summary>Session 数据</summary>
        public SessionData? Session { get; set; }
        
        /// <summary>Session ID</summary>
        public string? SessionId { get; set; }
        
        /// <summary>额外数据</summary>
        public Dictionary<string, object> Data { get; set; } = new();
        
        /// <summary>取消令牌</summary>
        public CancellationToken CancellationToken { get; set; }
    }
}