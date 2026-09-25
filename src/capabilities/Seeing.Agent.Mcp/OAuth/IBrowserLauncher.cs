namespace Seeing.Agent.Mcp.OAuth
{
    /// <summary>
    /// 浏览器打开抽象 — 用于 OAuth 授权流程中跳转授权 URL。
    /// <para>
    /// 非交互宿主（无浏览器/无回调能力）应返回 <c>false</c>，
    /// 由调用方显式提示用户手动打开并失败，绝不伪造令牌。
    /// </para>
    /// </summary>
    public interface IBrowserLauncher
    {
        /// <summary>尝试用系统默认浏览器打开 URL；成功返回 true，失败返回 false。</summary>
        bool TryOpen(string url);
    }
}
