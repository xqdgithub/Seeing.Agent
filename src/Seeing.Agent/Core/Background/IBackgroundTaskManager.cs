using Seeing.Agent.Core.Models;
using Seeing.Agent.Llm;

namespace Seeing.Agent.Core.Background;

/// <summary>
/// 后台任务管理器接口
/// </summary>
public interface IBackgroundTaskManager
{
    /// <summary>
    /// 启动后台任务
    /// </summary>
    /// <param name="args">任务启动参数</param>
    /// <returns>任务 ID</returns>
    Task<string> StartAsync(BackgroundTaskLaunchArgs args);
    
    /// <summary>
    /// 获取任务状态
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <returns>任务信息，不存在则返回 null</returns>
    Task<BackgroundTaskInfo?> GetAsync(string taskId);
    
    /// <summary>
    /// 获取任务输出
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <returns>任务输出内容，不存在则返回 null</returns>
    Task<string?> GetOutputAsync(string taskId);
    
    /// <summary>
    /// 取消任务
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <returns>是否成功取消</returns>
    Task<bool> CancelAsync(string taskId);
    
    /// <summary>
    /// 取消所有后台任务
    /// </summary>
    /// <returns>已取消的任务数量</returns>
    Task<int> CancelAllAsync();
    
    /// <summary>
    /// 列出所有任务
    /// </summary>
    /// <param name="status">可选的状态过滤</param>
    /// <returns>任务列表</returns>
    Task<IReadOnlyList<BackgroundTaskInfo>> ListAsync(BackgroundTaskStatus? status = null);
    
    /// <summary>
    /// 等待任务完成（阻塞）
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <param name="timeoutMs">超时时间（毫秒），默认 60000，最大 600000</param>
    /// <returns>任务信息</returns>
    Task<BackgroundTaskInfo?> WaitAsync(string taskId, int timeoutMs = 60000);
}