using Seeing.Agent.Abstractions.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Abstractions.Extensions
{
    /// <summary>
    /// 扩展接口 - 插件化扩展能力
    /// <para>
    /// 元数据与生命周期为核心契约；组件提供能力由细分扩展接口声明：
    /// IAgentExtension / IToolExtension / IHookExtension / IProviderExtension / IMcpExtension / ISkillPathExtension
    /// </para>
    /// </summary>
    public interface IExtension
    {
        #region 元数据

        /// <summary>
        /// 扩展唯一标识
        /// <para>可选，NuGet 包默认使用包名，文件插件必须提供</para>
        /// </summary>
        string? Id => null;

        /// <summary>
        /// 版本号
        /// </summary>
        string Version => "1.0.0";

        /// <summary>
        /// 显示名称
        /// </summary>
        string Name => "";

        /// <summary>
        /// 描述
        /// </summary>
        string Description => "";

        /// <summary>
        /// 目标运行时：server 或 tui
        /// <para>当前仅支持 server</para>
        /// </summary>
        string Target => "server";

        #endregion

        #region 生命周期

        /// <summary>
        /// 注册服务（DI 容器构建前调用）
        /// </summary>
        void ConfigureServices(IServiceCollection services) { }

        /// <summary>
        /// 初始化扩展（服务容器构建后调用）
        /// </summary>
        Task InitializeAsync(ExtensionContext context, ExtensionMeta meta)
            => Task.CompletedTask;

        /// <summary>
        /// 清理资源（停用时调用）
        /// </summary>
        Task DisposeAsync() => Task.CompletedTask;

        #endregion
    }
}