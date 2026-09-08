using Microsoft.Data.Sqlite;
using Seeing.Agent.Memory.Core.Schema.Migrations;

namespace Seeing.Agent.Memory.Core.Schema;

/// <summary>
/// Manages database schema migrations.
/// </summary>
public class SchemaManager
{
    private readonly SqliteConnection _connection;
    private readonly List<IMigration> _migrations;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaManager"/> class.
    /// </summary>
    /// <param name="connection">The SQLite connection.</param>
    public SchemaManager(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _migrations = new List<IMigration>
        {
            new V1_InitialSchema()
        };
    }

    /// <summary>
    /// Gets the current schema version.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current version, or 0 if no migrations have been applied.</returns>
    public async Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        // Check if schema_version table exists
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT name FROM sqlite_master 
            WHERE type='table' AND name='schema_version'";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null)
        {
            return 0;
        }

        // Get the max version
        command.CommandText = "SELECT MAX(version) FROM schema_version";
        var versionResult = await command.ExecuteScalarAsync(cancellationToken);
        
        return versionResult == null || Convert.IsDBNull(versionResult) 
            ? 0 
            : Convert.ToInt32(versionResult);
    }

    /// <summary>
    /// Applies all pending migrations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of migrations applied.</returns>
    public async Task<int> ApplyMigrationsAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = await GetCurrentVersionAsync(cancellationToken);
        var migrationsToApply = _migrations
            .Where(m => m.Version > currentVersion)
            .OrderBy(m => m.Version)
            .ToList();

        if (migrationsToApply.Count == 0)
        {
            return 0;
        }

        foreach (var migration in migrationsToApply)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await migration.ApplyAsync(_connection, cancellationToken);
        }

        return migrationsToApply.Count;
    }

    /// <summary>
    /// Checks if the schema is up to date.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if all migrations have been applied.</returns>
    public async Task<bool> IsSchemaUpToDateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = await GetCurrentVersionAsync(cancellationToken);
        var latestVersion = _migrations.Max(m => m.Version);
        return currentVersion >= latestVersion;
    }

    /// <summary>
    /// Gets the list of pending migrations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>List of pending migration descriptions.</returns>
    public async Task<IReadOnlyList<(int Version, string Description)>> GetPendingMigrationsAsync(
        CancellationToken cancellationToken = default)
    {
        var currentVersion = await GetCurrentVersionAsync(cancellationToken);
        
        return _migrations
            .Where(m => m.Version > currentVersion)
            .OrderBy(m => m.Version)
            .Select(m => (m.Version, m.Description))
            .ToList();
    }

    /// <summary>
    /// Ensures the schema is initialized. Idempotent operation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if migrations were applied, false if already up to date.</returns>
    public async Task<bool> EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        var applied = await ApplyMigrationsAsync(cancellationToken);
        return applied > 0;
    }
}
