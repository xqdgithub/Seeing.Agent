using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 扩展上下文
    /// </summary>
    public class ExtensionContext
    {
        public IServiceProvider Services { get; set; } = null!;
        public IConfiguration Configuration { get; set; } = null!;
        public Hooks.HookManager HookManager { get; set; } = null!;
        public Tools.ToolInvoker ToolInvoker { get; set; } = null!;
    }

    /// <summary>
    /// 扩展接口 - 插件化扩展能力
    /// </summary>
    public interface IExtension
    {
        /// <summary>扩展名称</summary>
        string Name { get; }
        
        /// <summary>扩展版本</summary>
        string Version { get; }
        
        /// <summary>初始化扩展</summary>
        Task InitializeAsync(ExtensionContext context);
        
        /// <summary>注册服务</summary>
        void ConfigureServices(IServiceCollection services);
        
        /// <summary>获取工具</summary>
        IEnumerable<ITool> GetTools();
        
        /// <summary>获取 Hook 处理器</summary>
        IEnumerable<IHookHandler> GetHookHandlers();
    }
}
