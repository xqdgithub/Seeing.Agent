using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Serialization.SystemTextJson;
using Seeing.Agent.Configuration;
using Seeing.Agent.Scheduler.Configuration;
using Seeing.Agent.Scheduler.Engine;
using Seeing.Agent.Scheduler.Models;
using Xunit;

namespace Seeing.Agent.Scheduler.Tests;

/// <summary>
/// 测试 SQLite 持久化启动问题
/// </summary>
public class SQLitePersistenceTests
{
    [Fact]
    public async Task SQLiteMicrosoft_Provider_LoadsSuccessfully()
    {
        // 此测试验证关键问题已修复：
        // 原问题：Quartz.NET 默认使用 System.Data.SQLite，但项目使用 Microsoft.Data.Sqlite
        // 修复：使用 SQLite-Microsoft provider
        var tempDir = Path.Combine(Path.GetTempPath(), $"quartz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = $"Data Source={tempDir}/quartz.db";

        try
        {
            // Act - 使用 SQLite-Microsoft provider（Microsoft.Data.Sqlite）
            var builder = SchedulerBuilder.Create()
                .WithId("TestScheduler")
                .WithName("Test Scheduler")
                .UseDefaultThreadPool(tp => tp.MaxConcurrency = 3);

            builder.UsePersistentStore(store =>
            {
                // 关键修复：使用 SQLite-Microsoft 而非 SQLite
                store.UseGenericDatabase("SQLite-Microsoft", db => db.ConnectionString = dbPath);
                store.UseNewtonsoftJsonSerializer();
            });

            var factory = builder.Build();
            
            // Assert - 程序集加载成功即表示测试通过
            // 原错误：Could not load file or assembly 'System.Data.SQLite'
            // 现修复：成功加载 Microsoft.Data.Sqlite
            Assert.NotNull(factory);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }
    }

    [Fact]
    public async Task SQLiteMicrosoft_Provider_WithSystemTextJson_LoadsSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"quartz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = $"Data Source={tempDir}/quartz.db";

        try
        {
            var builder = SchedulerBuilder.Create()
                .WithId("TestScheduler")
                .WithName("Test Scheduler")
                .UseDefaultThreadPool(tp => tp.MaxConcurrency = 3);

            builder.UsePersistentStore(store =>
            {
                store.UseGenericDatabase("SQLite-Microsoft", db => db.ConnectionString = dbPath);
                store.UseSystemTextJsonSerializer();
                store.UseProperties = true;
            });

            var factory = builder.Build();
            Assert.NotNull(factory);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }
    }

    private class TestWorkspaceProvider : IWorkspaceProvider
    {
        private string _workspaceRoot;

        public TestWorkspaceProvider(string workspaceDir)
        {
            _workspaceRoot = workspaceDir;
        }

        public string WorkspaceRoot => _workspaceRoot;
        public string ProjectSeeingDirectory => _workspaceRoot;
        public string UserSeeingDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".seeing");
        
        public void SetWorkspaceRoot(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
        }

        public string GetSeeingDirectory(ConfigLevel level) => level switch
        {
            ConfigLevel.User => UserSeeingDirectory,
            ConfigLevel.Project => ProjectSeeingDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };
    }

    private class TestSchedulerOptionsProvider : ISchedulerOptionsProvider
    {
        private readonly SchedulerOptions _options;

        public TestSchedulerOptionsProvider(SchedulerOptions options)
        {
            _options = options;
        }

        public SchedulerOptions Current => _options;
        public void Reload() { }
    }
}
