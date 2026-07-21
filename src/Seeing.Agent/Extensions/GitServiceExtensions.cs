using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Git;
using Seeing.Agent.Git.Tools;
using Seeing.Agent.Tools;

namespace Seeing.Agent.Extensions
{
    /// <summary>
    /// Git 服务 DI 扩展
    /// </summary>
    public static class GitServiceExtensions
    {
        /// <summary>添加 Git 服务</summary>
        public static IServiceCollection AddGitServices(
            this IServiceCollection services,
            string? workingDirectory = null)
        {
            // 配置
            services.Configure<GitOptions>(options =>
            {
                if (workingDirectory != null)
                    options.WorkingDirectory = workingDirectory;
            });

            // 服务
            services.AddSingleton<IGitService, GitService>();

            // 工具
            services.AddSingleton<GitStatusTool>();
            services.AddSingleton<GitDiffTool>();
            services.AddSingleton<GitLogTool>();
            services.AddSingleton<GitCommitTool>();

            return services;
        }

        /// <summary>注册 Git 工具到 ToolManager</summary>
        public static void RegisterGitTools(
            this ToolManager toolInvoker,
            IServiceProvider serviceProvider)
        {
            toolInvoker.RegisterTool(serviceProvider.GetRequiredService<GitStatusTool>());
            toolInvoker.RegisterTool(serviceProvider.GetRequiredService<GitDiffTool>());
            toolInvoker.RegisterTool(serviceProvider.GetRequiredService<GitLogTool>());
            toolInvoker.RegisterTool(serviceProvider.GetRequiredService<GitCommitTool>());
        }
    }
}
