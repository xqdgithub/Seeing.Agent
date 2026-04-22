using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Extensions;

namespace Seeing.Agent.Host;

// 引导程序：通过 Generic Host 配置与启动 Seeing.Agent，最终通过反射执行 MainApp.Run()
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // 1) 构建基础配置（appsettings.json + 环境变量）
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // 2) 使用 Generic Host 注册 Seeing.Agent 服务
        using IHost host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, config) =>
            {
                // 将外部配置合并到主配置中
                config.AddConfiguration(configuration);
            })
            .ConfigureServices((context, services) =>
            {
                // 基于配置注册 Seeing.Agent 核心服务
                services.AddSeeingAgent(configuration);
            })
            .ConfigureLogging(b =>
            {
                b.AddConsole();
                b.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        // 3) 通过反射定位并执行 MainApp.Run()
        try
        {
            // 查找名称为 MainApp 的类型（不强依赖程序集名）
            Type? mainAppType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                mainAppType = asm.GetTypes().FirstOrDefault(t => t.Name == "MainApp");
                if (mainAppType != null) break;
            }

            if (mainAppType == null)
            {
                Console.WriteLine("未找到 MainApp 类型，无法执行 Run().");
                return 1;
            }

            // 尝试调用 Run()（无参数）或 RunAsync(CancellationToken)（带取消令牌）
            var runMethod = mainAppType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (runMethod == null)
            {
                // 尝试 RunAsync
                var runAsync = mainAppType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "RunAsync");

                if (runAsync == null)
                {
                    Console.WriteLine("MainApp.Run/RunAsync 未找到，无法执行入口。");
                    return 1;
                }

                var p = runAsync.GetParameters();
                object?[] callParams = p.Length == 1 ? new object?[] { CancellationToken.None } : new object?[] { };
                var task = (Task)runAsync.Invoke(null, callParams)!;
                await task.ConfigureAwait(false);
                return 0;
            }

            // 直接调用 Run()
            var result = runMethod.Invoke(null, null);
            if (result is Task taskResult)
            {
                await taskResult.ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动失败: {ex.Message}");
            return 1;
        }
        finally
        {
            // 优雅退出
            try
            if (host is IAsyncDisposable asyncDisp)
                await asyncDisp.DisposeAsync();
            else
                host.Dispose();
        }
    }
}
