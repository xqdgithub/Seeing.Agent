using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Abstractions;
using Seeing.Agent.Scheduler.Models;

namespace Seeing.Agent.Scheduler.Persistence;

/// <summary>JSON 文件持久化（jobs.json + jobs_history/）</summary>
public sealed class JsonScheduleRepository : IScheduleRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IWorkspaceProvider _workspace;
    private readonly ILogger<JsonScheduleRepository> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

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
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(JobsFilePath))
                return new JobsFile();

            var json = await File.ReadAllTextAsync(JobsFilePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JobsFile>(json, JsonOptions) ?? new JobsFile();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(JobsFile jobs, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JobsFilePath)!);
            var tmpPath = JobsFilePath + ".tmp";
            var payload = JsonSerializer.Serialize(jobs, JsonOptions);
            await File.WriteAllTextAsync(tmpPath, payload, ct).ConfigureAwait(false);
            File.Move(tmpPath, JobsFilePath, overwrite: true);
            _logger.LogDebug("Saved jobs file: {Path}", JobsFilePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task AppendHistoryAsync(string jobId, JobExecutionRecord record, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            var path = Path.Combine(HistoryDirectory, $"{SanitizeFileName(jobId)}.json");

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

            var tmpPath = path + ".tmp";
            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(history, JsonOptions), ct).ConfigureAwait(false);
            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobExecutionRecord>> GetHistoryAsync(string jobId, int limit, CancellationToken ct = default)
    {
        var path = Path.Combine(HistoryDirectory, $"{SanitizeFileName(jobId)}.json");
        if (!File.Exists(path))
            return Array.Empty<JobExecutionRecord>();

        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var history = JsonSerializer.Deserialize<List<JobExecutionRecord>>(json, JsonOptions) ?? new();
        return history.Take(Math.Max(1, limit)).ToList();
    }

    private static string SanitizeFileName(string jobId) =>
        string.Concat(jobId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
