using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Tools.BuiltIn.Plan
{
    /// <summary>
    /// 计划管理器
    /// </summary>
    public class PlanManager
    {
        private readonly ILogger<PlanManager> _logger;
        private readonly ConcurrentDictionary<string, PlanModel> _plans = new();
        private readonly string _storagePath;

        public PlanManager(ILogger<PlanManager> logger, string? storagePath = null)
        {
            _logger = logger;
            _storagePath = storagePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".seeing", "plans");
        }

        /// <summary>创建计划</summary>
        public async Task<PlanModel> CreatePlanAsync(
            string name,
            string description,
            string? sessionId = null,
            CancellationToken ct = default)
        {
            var plan = new PlanModel
            {
                Name = name,
                Description = description,
                SessionId = sessionId,
                Status = PlanStatus.Draft
            };

            _plans[plan.Id] = plan;
            await SavePlanAsync(plan, ct);

            _logger.LogInformation("Created plan {PlanId}: {Name}", plan.Id, name);
            return plan;
        }

        /// <summary>获取计划</summary>
        public Task<PlanModel?> GetPlanAsync(string planId, CancellationToken ct = default)
        {
            if (_plans.TryGetValue(planId, out var plan))
                return Task.FromResult<PlanModel?>(plan);

            // 尝试从文件加载
            var path = GetPlanPath(planId);
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    plan = JsonSerializer.Deserialize<PlanModel>(json);
                    if (plan != null)
                    {
                        _plans[planId] = plan;
                        return Task.FromResult<PlanModel?>(plan);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load plan: {PlanId}", planId);
                }
            }

            return Task.FromResult<PlanModel?>(null);
        }

        /// <summary>更新计划</summary>
        public async Task<bool> UpdatePlanAsync(PlanModel plan, CancellationToken ct = default)
        {
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            _plans[plan.Id] = plan;
            await SavePlanAsync(plan, ct);
            return true;
        }

        /// <summary>删除计划</summary>
        public Task<bool> DeletePlanAsync(string planId, CancellationToken ct = default)
        {
            _plans.TryRemove(planId, out _);
            var path = GetPlanPath(planId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Task.FromResult(true);
        }

        /// <summary>列出所有计划</summary>
        public Task<IReadOnlyList<PlanModel>> ListPlansAsync(
            string? sessionId = null,
            PlanStatus? status = null,
            CancellationToken ct = default)
        {
            var plans = _plans.Values.AsEnumerable();

            if (sessionId != null)
                plans = plans.Where(p => p.SessionId == sessionId);

            if (status != null)
                plans = plans.Where(p => p.Status == status);

            return Task.FromResult<IReadOnlyList<PlanModel>>(
                plans.OrderByDescending(p => p.UpdatedAt).ToList());
        }

        /// <summary>添加任务</summary>
        public async Task<PlanTask> AddTaskAsync(
            string planId,
            string title,
            string? description = null,
            int priority = 0,
            List<string>? dependencies = null,
            CancellationToken ct = default)
        {
            var plan = await GetPlanAsync(planId, ct);
            if (plan == null)
                throw new InvalidOperationException($"Plan not found: {planId}");

            var task = new PlanTask
            {
                Title = title,
                Description = description,
                Priority = priority,
                Dependencies = dependencies ?? new List<string>()
            };

            plan.Tasks.Add(task);
            await UpdatePlanAsync(plan, ct);

            _logger.LogInformation("Added task {TaskId} to plan {PlanId}: {Title}", task.Id, planId, title);
            return task;
        }

        /// <summary>更新任务状态</summary>
        public async Task<bool> UpdateTaskStatusAsync(
            string planId,
            string taskId,
            PlanTaskStatus status,
            string? result = null,
            CancellationToken ct = default)
        {
            var plan = await GetPlanAsync(planId, ct);
            if (plan == null) return false;

            var task = plan.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return false;

            task.Status = status;
            if (status == PlanTaskStatus.Completed)
            {
                task.CompletedAt = DateTimeOffset.UtcNow;
            }
            if (result != null)
            {
                task.Result = result;
            }

            // 检查计划是否完成
            if (plan.Tasks.All(t => t.Status == PlanTaskStatus.Completed || t.Status == PlanTaskStatus.Skipped))
            {
                plan.Status = PlanStatus.Completed;
            }
            else if (plan.Tasks.Any(t => t.Status == PlanTaskStatus.InProgress))
            {
                plan.Status = PlanStatus.InProgress;
            }

            await UpdatePlanAsync(plan, ct);
            return true;
        }

        /// <summary>获取下一个可执行任务</summary>
        public async Task<PlanTask?> GetNextTaskAsync(string planId, CancellationToken ct = default)
        {
            var plan = await GetPlanAsync(planId, ct);
            if (plan == null) return null;

            var completedIds = plan.Tasks
                .Where(t => t.Status == PlanTaskStatus.Completed)
                .Select(t => t.Id)
                .ToHashSet();

            return plan.Tasks
                .Where(t => t.Status == PlanTaskStatus.Pending)
                .Where(t => t.Dependencies.All(d => completedIds.Contains(d)))
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => plan.Tasks.IndexOf(t))
                .FirstOrDefault();
        }

        private async Task SavePlanAsync(PlanModel plan, CancellationToken ct)
        {
            Directory.CreateDirectory(_storagePath);
            var path = GetPlanPath(plan.Id);
            var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, ct);
        }

        private string GetPlanPath(string planId) => Path.Combine(_storagePath, $"{planId}.json");
    }
}
