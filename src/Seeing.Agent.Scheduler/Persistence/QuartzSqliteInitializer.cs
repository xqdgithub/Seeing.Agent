using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Seeing.Agent.Scheduler.Persistence;

/// <summary>SQLite 数据库初始化器 - 自动创建 Quartz.NET 所需的表</summary>
public static class QuartzSqliteInitializer
{
    private static readonly string[] CreateTablesSql = new[]
    {
        // QRTZ_JOB_DETAILS
        @"CREATE TABLE IF NOT EXISTS QRTZ_JOB_DETAILS
        (
            SCHED_NAME TEXT NOT NULL,
            JOB_NAME TEXT NOT NULL,
            JOB_GROUP TEXT NOT NULL,
            DESCRIPTION TEXT NULL,
            JOB_CLASS_NAME TEXT NOT NULL,
            IS_DURABLE INTEGER NOT NULL,
            IS_NONCONCURRENT INTEGER NOT NULL,
            IS_UPDATE_DATA INTEGER NOT NULL,
            REQUESTS_RECOVERY INTEGER NOT NULL,
            JOB_DATA BLOB NULL,
            PRIMARY KEY (SCHED_NAME, JOB_NAME, JOB_GROUP)
        )",

        // QRTZ_TRIGGERS
        @"CREATE TABLE IF NOT EXISTS QRTZ_TRIGGERS
        (
            SCHED_NAME TEXT NOT NULL,
            TRIGGER_NAME TEXT NOT NULL,
            TRIGGER_GROUP TEXT NOT NULL,
            JOB_NAME TEXT NOT NULL,
            JOB_GROUP TEXT NOT NULL,
            DESCRIPTION TEXT NULL,
            NEXT_FIRE_TIME INTEGER NULL,
            PREV_FIRE_TIME INTEGER NULL,
            PRIORITY INTEGER NULL,
            TRIGGER_STATE TEXT NOT NULL,
            TRIGGER_TYPE TEXT NOT NULL,
            START_TIME INTEGER NOT NULL,
            END_TIME INTEGER NULL,
            CALENDAR_NAME TEXT NULL,
            MISFIRE_INSTR INTEGER NULL,
            MISFIRE_ORIG_FIRE_TIME INTEGER NULL,
            EXECUTION_GROUP TEXT NULL,
            JOB_DATA BLOB NULL,
            PRIMARY KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP),
            FOREIGN KEY (SCHED_NAME, JOB_NAME, JOB_GROUP)
                REFERENCES QRTZ_JOB_DETAILS(SCHED_NAME, JOB_NAME, JOB_GROUP)
        )",

        // QRTZ_SIMPLE_TRIGGERS
        @"CREATE TABLE IF NOT EXISTS QRTZ_SIMPLE_TRIGGERS
        (
            SCHED_NAME TEXT NOT NULL,
            TRIGGER_NAME TEXT NOT NULL,
            TRIGGER_GROUP TEXT NOT NULL,
            REPEAT_COUNT INTEGER NOT NULL,
            REPEAT_INTERVAL INTEGER NOT NULL,
            TIMES_TRIGGERED INTEGER NOT NULL,
            PRIMARY KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP),
            FOREIGN KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP)
                REFERENCES QRTZ_TRIGGERS(SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP) ON DELETE CASCADE
        )",

        // QRTZ_SIMPROP_TRIGGERS
        @"CREATE TABLE IF NOT EXISTS QRTZ_SIMPROP_TRIGGERS
        (
            SCHED_NAME TEXT NOT NULL,
            TRIGGER_NAME TEXT NOT NULL,
            TRIGGER_GROUP TEXT NOT NULL,
            STR_PROP_1 TEXT NULL,
            STR_PROP_2 TEXT NULL,
            STR_PROP_3 TEXT NULL,
            INT_PROP_1 INTEGER NULL,
            INT_PROP_2 INTEGER NULL,
            LONG_PROP_1 INTEGER NULL,
            LONG_PROP_2 INTEGER NULL,
            DEC_PROP_1 NUMERIC NULL,
            DEC_PROP_2 NUMERIC NULL,
            BOOL_PROP_1 INTEGER NULL,
            BOOL_PROP_2 INTEGER NULL,
            TIME_ZONE_ID TEXT NULL,
            PRIMARY KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP),
            FOREIGN KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP)
                REFERENCES QRTZ_TRIGGERS(SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP) ON DELETE CASCADE
        )",

        // QRTZ_CRON_TRIGGERS
        @"CREATE TABLE IF NOT EXISTS QRTZ_CRON_TRIGGERS
        (
            SCHED_NAME TEXT NOT NULL,
            TRIGGER_NAME TEXT NOT NULL,
            TRIGGER_GROUP TEXT NOT NULL,
            CRON_EXPRESSION TEXT NOT NULL,
            TIME_ZONE_ID TEXT NULL,
            PRIMARY KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP),
            FOREIGN KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP)
                REFERENCES QRTZ_TRIGGERS(SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP) ON DELETE CASCADE
        )",

        // QRTZ_BLOB_TRIGGERS
        @"CREATE TABLE IF NOT EXISTS QRTZ_BLOB_TRIGGERS
        (
            SCHED_NAME TEXT NOT NULL,
            TRIGGER_NAME TEXT NOT NULL,
            TRIGGER_GROUP TEXT NOT NULL,
            BLOB_DATA BLOB NULL,
            PRIMARY KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP),
            FOREIGN KEY (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP)
                REFERENCES QRTZ_TRIGGERS(SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP) ON DELETE CASCADE
        )",

        // QRTZ_CALENDARS
        @"CREATE TABLE IF NOT EXISTS QRTZ_CALENDARS
        (
            SCHED_NAME TEXT NOT NULL,
            CALENDAR_NAME TEXT NOT NULL,
            CALENDAR BLOB NOT NULL,
            PRIMARY KEY (SCHED_NAME, CALENDAR_NAME)
        )",

        // QRTZ_PAUSED_TRIGGER_GRPS
        @"CREATE TABLE IF NOT EXISTS QRTZ_PAUSED_TRIGGER_GRPS
        (
            SCHED_NAME TEXT NOT NULL,
            TRIGGER_GROUP TEXT NOT NULL,
            PRIMARY KEY (SCHED_NAME, TRIGGER_GROUP)
        )",

        // QRTZ_FIRED_TRIGGERS
        @"CREATE TABLE IF NOT EXISTS QRTZ_FIRED_TRIGGERS
        (
            SCHED_NAME TEXT NOT NULL,
            ENTRY_ID TEXT NOT NULL,
            TRIGGER_NAME TEXT NOT NULL,
            TRIGGER_GROUP TEXT NOT NULL,
            INSTANCE_NAME TEXT NOT NULL,
            FIRED_TIME INTEGER NOT NULL,
            SCHED_TIME INTEGER NOT NULL,
            PRIORITY INTEGER NOT NULL,
            STATE TEXT NOT NULL,
            JOB_NAME TEXT NULL,
            JOB_GROUP TEXT NULL,
            IS_NONCONCURRENT INTEGER NULL,
            REQUESTS_RECOVERY INTEGER NULL,
            EXECUTION_GROUP TEXT NULL,
            PRIMARY KEY (SCHED_NAME, ENTRY_ID)
        )",

        // QRTZ_SCHEDULER_STATE
        @"CREATE TABLE IF NOT EXISTS QRTZ_SCHEDULER_STATE
        (
            SCHED_NAME TEXT NOT NULL,
            INSTANCE_NAME TEXT NOT NULL,
            LAST_CHECKIN_TIME INTEGER NOT NULL,
            CHECKIN_INTERVAL INTEGER NOT NULL,
            PRIMARY KEY (SCHED_NAME, INSTANCE_NAME)
        )",

        // QRTZ_LOCKS
        @"CREATE TABLE IF NOT EXISTS QRTZ_LOCKS
        (
            SCHED_NAME TEXT NOT NULL,
            LOCK_NAME TEXT NOT NULL,
            PRIMARY KEY (SCHED_NAME, LOCK_NAME)
        )"
    };

    private static readonly string[] CreateTriggersSql = new[]
    {
        // Triggers for cascade delete
        @"CREATE TRIGGER IF NOT EXISTS DELETE_SIMPLE_TRIGGER
        AFTER DELETE ON QRTZ_TRIGGERS
        BEGIN
            DELETE FROM QRTZ_SIMPLE_TRIGGERS
            WHERE SCHED_NAME = OLD.SCHED_NAME
              AND TRIGGER_NAME = OLD.TRIGGER_NAME
              AND TRIGGER_GROUP = OLD.TRIGGER_GROUP;
        END",

        @"CREATE TRIGGER IF NOT EXISTS DELETE_SIMPROP_TRIGGER
        AFTER DELETE ON QRTZ_TRIGGERS
        BEGIN
            DELETE FROM QRTZ_SIMPROP_TRIGGERS
            WHERE SCHED_NAME = OLD.SCHED_NAME
              AND TRIGGER_NAME = OLD.TRIGGER_NAME
              AND TRIGGER_GROUP = OLD.TRIGGER_GROUP;
        END",

        @"CREATE TRIGGER IF NOT EXISTS DELETE_CRON_TRIGGER
        AFTER DELETE ON QRTZ_TRIGGERS
        BEGIN
            DELETE FROM QRTZ_CRON_TRIGGERS
            WHERE SCHED_NAME = OLD.SCHED_NAME
              AND TRIGGER_NAME = OLD.TRIGGER_NAME
              AND TRIGGER_GROUP = OLD.TRIGGER_GROUP;
        END",

        @"CREATE TRIGGER IF NOT EXISTS DELETE_BLOB_TRIGGER
        AFTER DELETE ON QRTZ_TRIGGERS
        BEGIN
            DELETE FROM QRTZ_BLOB_TRIGGERS
            WHERE SCHED_NAME = OLD.SCHED_NAME
              AND TRIGGER_NAME = OLD.TRIGGER_NAME
              AND TRIGGER_GROUP = OLD.TRIGGER_GROUP;
        END"
    };

    /// <summary>初始化 Quartz.NET SQLite 数据库表结构</summary>
    public static async Task InitializeAsync(string connectionString, ILogger? logger = null, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        // 检查表是否已存在
        var tablesExist = await CheckTablesExistAsync(connection, ct);
        if (tablesExist)
        {
            logger?.LogDebug("Quartz tables already exist, skipping initialization");
            return;
        }

        logger?.LogInformation("Initializing Quartz.NET SQLite database schema...");

        // 创建所有表
        foreach (var sql in CreateTablesSql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        // 创建所有触发器
        foreach (var sql in CreateTriggersSql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        logger?.LogInformation("Quartz.NET SQLite database schema initialized successfully");
    }

    private static async Task<bool> CheckTablesExistAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN (
                'QRTZ_JOB_DETAILS', 'QRTZ_TRIGGERS', 'QRTZ_SIMPLE_TRIGGERS',
                'QRTZ_CRON_TRIGGERS', 'QRTZ_FIRED_TRIGGERS', 'QRTZ_SCHEDULER_STATE'
            )";

        var count = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        return count >= 6; // 至少6个核心表存在
    }
}
