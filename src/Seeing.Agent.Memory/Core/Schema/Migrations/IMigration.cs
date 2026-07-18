using Microsoft.Data.Sqlite;

namespace Seeing.Agent.Memory.Core.Schema.Migrations;

/// <summary>
/// Represents a database schema migration.
/// </summary>
public interface IMigration
{
    /// <summary>
    /// Gets the migration version number.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Gets the migration description.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Applies the migration to the database.
    /// </summary>
    /// <param name="connection">The SQLite connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default);
}
