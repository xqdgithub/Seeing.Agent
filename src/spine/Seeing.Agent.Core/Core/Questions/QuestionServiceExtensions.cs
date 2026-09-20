using Microsoft.Extensions.DependencyInjection;
using Seeing.Agent.Abstractions.Interactions;
using Seeing.Agent.Abstractions.Questions;
using Seeing.Agent.Core.Interactions;

namespace Seeing.Agent.Core.Questions;

/// <summary>
/// 问答服务扩展 - 注册可呈现性注册表（独立实例）与在途问答管理器。
/// </summary>
public static class QuestionServiceExtensions
{
    /// <summary>
    /// 注册问答交互基础设施（呈现端注册表与在途管理器均为 Singleton）。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddSeeingQuestions(this IServiceCollection services)
    {
        // 与权限分离的独立实例，避免 Gateway 的呈现端让问答误判为可呈现
        services.AddSingleton<IQuestionSurfaceRegistry>(_ => new SurfaceRegistry());

        services.AddSingleton<IQuestionRequestManager, QuestionRequestManager>();

        return services;
    }
}
