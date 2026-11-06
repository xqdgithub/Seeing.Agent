using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;
using Seeing.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seeing.Agent.Scheduler.Persistence;

/// <summary>
/// JSON 文件持久化（jobs.json + jobs_history/）。
/// <para>
/// 所有写入都走 <see cref="ScheduleAtomicFile"/>：**跨实例**的按路径锁 + 唯一临时名 + 移动重试。
/// 不使用实例级 <c>SemaphoreSlim</c> —— 它只在单实例内有效，而同一份文件会被
/// DI 单例、CLI、冷/热实例等多方访问（这正是历史上 <c>*.json.tmp</c> 丢失的成因）。
/// </para>
/// </summary>
public sealed class JsonScheduleRepository : IScheduleRepository
{
    private static readonly JsonSerializerOptions JsonOptions = SeeingJson.Create(indented: true, o =>
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Converters.Add(new JsonStringEnumConverter());
    });

    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<JsonScheduleRepository> _logger;

    public JsonScheduleRepository(IWorkspaceProvider workspace, ILogger<JsonScheduleRepository> logger)
    {
        _workspace = workspace;
        _logger = logger;
    }

    private string JobsFilePath => Path.Combine(_workspace.ProjectSeeingDirectory, "jobs.json");
    private string HistoryDirectory => Path.Combine(_workspace.ProjectSeeingDirectory, "jobs_history");

    /// <inheritdoc/>
    public async Task<JobsFile> LoadAsync(CancellationToken ct = default)
    {
        var path = JobsFilePath;
        var gate = ScheduleAtomicFile.GetLock(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return new JobsFile();

            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JobsFile>(json, JsonOptions) ?? new JobsFile();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(JobsFile jobs, CancellationToken ct = default)
    {
        var path = JobsFilePath;
        var gate = ScheduleAtomicFile.GetLock(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        string? tmpPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(jobs, JsonOptions);

            tmpPath = ScheduleAtomicFile.CreateTempPath(path);
            await File.WriteAllTextAsync(tmpPath, payload, ct).ConfigureAwait(false);
            await ScheduleAtomicFile.ReplaceAsync(tmpPath, path, _logger, ct).ConfigureAwait(false);
            tmpPath = null; // 已消费

            _logger.LogDebug("Saved jobs file: {Path}", path);
        }
        finally
        {
            // 失败时清理唯一临时文件；唯一名字保证不会误删他人正在写的文件
            if (tmpPath is not null)
                ScheduleAtomicFile.TryDelete(tmpPath);

            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task AppendHistoryAsync(string jobId, JobExecutionRecord record, CancellationToken ct = default)
    {
        Directory.CreateDirectory(HistoryDirectory);
        var path = Path.Combine(HistoryDirectory, $"{SanitizeFileName(jobId)}.json");

        // 锁必须覆盖整个「读-改-写」周期，否则两个写入者会基于同一份旧内容各自覆盖
        var gate = ScheduleAtomicFile.GetLock(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        string? tmpPath = null;
        try
        {
            List<JobExecutionRecord> history;
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                history = JsonSerializer.Deserialize<List<JobExecutionRecord>>(json, JsonOptions) ?? new();
            }
            else
            {
                history = new List<JobExecutionRecord>();
            }

            history.Insert(0, record);
            if (history.Count > SchedulerConstants.MaxHistoryRecords)
                history = history.Take(SchedulerConstants.MaxHistoryRecords).ToList();

            tmpPath = ScheduleAtomicFile.CreateTempPath(path);
            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(history, JsonOptions), ct)
                .ConfigureAwait(false);
            await ScheduleAtomicFile.ReplaceAsync(tmpPath, path, _logger, ct).ConfigureAwait(false);
            tmpPath = null; // 已消费
        }
        finally
        {
            if (tmpPath is not null)
                ScheduleAtomicFile.TryDelete(tmpPath);

            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(string jobId, int limit, int offset = 0, CancellationToken ct = default)
    {
        var path = Path.Combine(HistoryDirectory, $"{SanitizeFileName(jobId)}.json");

        // 与写入共享同一把锁：避免读到「已截断但未写完」的中间态
        var gate = ScheduleAtomicFile.GetLock(path);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return Array.Empty<JobExecutionRecord>();

            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var history = JsonSerializer.Deserialize<List<JobExecutionRecord>>(json, JsonOptions) ?? new();
            return history.Skip(Math.Max(0, offset)).Take(Math.Max(1, limit)).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    private static string SanitizeFileName(string jobId) =>
        string.Concat(jobId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
