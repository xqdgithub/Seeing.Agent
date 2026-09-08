using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Core.Schema;
using Xunit;

namespace Seeing.Agent.Memory.Tests.Schema;

public class SchemaManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SchemaManager _schemaManager;

    public SchemaManagerTests()
    {
        // Create in-memory database for testing
        var connectionBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory
        };
        
        _connection = new SqliteConnection(connectionBuilder.ConnectionString);
        _connection.Open();
        _schemaManager = new SchemaManager(_connection);
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Fact]
    public async Task GetCurrentVersion_WhenNoSchema_ReturnsZero()
    {
        // Act
        var version = await _schemaManager.GetCurrentVersionAsync();

        // Assert
        Assert.Equal(0, version);
    }

    [Fact]
    public async Task ApplyMigrations_AppliesAllPendingMigrations()
    {
        // Act
        var applied = await _schemaManager.ApplyMigrationsAsync();

        // Assert
        Assert.Equal(1, applied);
        var version = await _schemaManager.GetCurrentVersionAsync();
        Assert.Equal(1, version);
    }

    [Fact]
    public async Task ApplyMigrations_WhenAlreadyApplied_ReturnsZero()
    {
        // Arrange
        await _schemaManager.ApplyMigrationsAsync();

        // Act
        var applied = await _schemaManager.ApplyMigrationsAsync();

        // Assert
        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task IsSchemaUpToDate_WhenNoSchema_ReturnsFalse()
    {
        // Act
        var isUpToDate = await _schemaManager.IsSchemaUpToDateAsync();

        // Assert
        Assert.False(isUpToDate);
    }

    [Fact]
    public async Task IsSchemaUpToDate_WhenSchemaApplied_ReturnsTrue()
    {
        // Arrange
        await _schemaManager.ApplyMigrationsAsync();

        // Act
        var isUpToDate = await _schemaManager.IsSchemaUpToDateAsync();

        // Assert
        Assert.True(isUpToDate);
    }

    [Fact]
    public async Task GetPendingMigrations_ReturnsAllMigrations_WhenNoSchema()
    {
        // Act
        var pending = await _schemaManager.GetPendingMigrationsAsync();

        // Assert
        Assert.Single(pending);
        Assert.Equal(1, pending[0].Version);
    }

    [Fact]
    public async Task GetPendingMigrations_ReturnsEmpty_WhenSchemaApplied()
    {
        // Arrange
        await _schemaManager.ApplyMigrationsAsync();

        // Act
        var pending = await _schemaManager.GetPendingMigrationsAsync();

        // Assert
        Assert.Empty(pending);
    }

    [Fact]
    public async Task EnsureInitialized_AppliesMigrationsAndReturnsTrue()
    {
        // Act
        var initialized = await _schemaManager.EnsureInitializedAsync();

        // Assert
        Assert.True(initialized);
        var version = await _schemaManager.GetCurrentVersionAsync();
        Assert.Equal(1, version);
    }

    [Fact]
    public async Task EnsureInitialized_IsIdempotent()
    {
        // Arrange
        await _schemaManager.EnsureInitializedAsync();

        // Act
        var initialized = await _schemaManager.EnsureInitializedAsync();

        // Assert
        Assert.False(initialized); // No new migrations applied
    }

    [Fact]
    public async Task Schema_CreatesAllRequiredTables()
    {
        // Act
        await _schemaManager.ApplyMigrationsAsync();

        // Assert - Verify all tables exist
        var tables = await GetTableNamesAsync();
        
        Assert.Contains("schema_version", tables);
        Assert.Contains("vectors", tables);
        Assert.Contains("keywords", tables);
        Assert.Contains("links", tables);
        Assert.Contains("embedding_cache", tables);
        Assert.Contains("token_usage", tables);
        Assert.Contains("quota_state", tables);
    }

    [Fact]
    public async Task VectorsTable_HasCorrectSchema()
    {
        // Act
        await _schemaManager.ApplyMigrationsAsync();

        // Assert - Verify vectors table columns
        var columns = await GetTableColumnsAsync("vectors");
        
        Assert.Contains(("id", "TEXT"), columns);
        Assert.Contains(("content", "TEXT"), columns);
        Assert.Contains(("embedding", "BLOB"), columns);
        Assert.Contains(("metadata", "TEXT"), columns);
        Assert.Contains(("created_at", "TEXT"), columns);
        Assert.Contains(("updated_at", "TEXT"), columns);
    }

    [Fact]
    public async Task LinksTable_HasCorrectSchema()
    {
        // Act
        await _schemaManager.ApplyMigrationsAsync();

        // Assert - Verify links table columns
        var columns = await GetTableColumnsAsync("links");
        
        Assert.Contains(("id", "INTEGER"), columns);
        Assert.Contains(("source_id", "TEXT"), columns);
        Assert.Contains(("target_id", "TEXT"), columns);
        Assert.Contains(("relation_type", "TEXT"), columns);
        Assert.Contains(("weight", "REAL"), columns);
        Assert.Contains(("metadata", "TEXT"), columns);
        Assert.Contains(("created_at", "TEXT"), columns);
    }

    [Fact]
    public async Task KeywordsTable_IsVirtualTable()
    {
        // Act
        await _schemaManager.ApplyMigrationsAsync();

        // Assert
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT type FROM sqlite_master 
            WHERE type='table' AND name='keywords'";
        
        var tableType = await command.ExecuteScalarAsync();
        Assert.Equal("table", tableType);
        
        // Verify it's a virtual table by checking sql contains "VIRTUAL TABLE"
        command.CommandText = @"
            SELECT sql FROM sqlite_master 
            WHERE type='table' AND name='keywords'";
        
        var sql = await command.ExecuteScalarAsync() as string;
        Assert.Contains("VIRTUAL TABLE", sql);
    }

    [Fact]
    public async Task SchemaVersion_RecordsMigration()
    {
        // Act
        await _schemaManager.ApplyMigrationsAsync();

        // Assert
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version, description FROM schema_version WHERE version = 1";
        
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Initial schema", reader.GetString(1));
    }

    private async Task<List<string>> GetTableNamesAsync()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        
        var tables = new List<string>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private async Task<List<(string Name, string Type)>> GetTableColumnsAsync(string tableName)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        
        var columns = new List<(string, string)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1);
            var type = reader.GetString(2);
            columns.Add((name, type));
        }
        return columns;
    }
}
