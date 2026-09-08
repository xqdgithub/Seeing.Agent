using Microsoft.Data.Sqlite;

namespace Seeing.Agent.Memory.Core.Schema.Migrations;

/// <summary>
/// Initial schema migration (V1).
/// Creates all core tables for the memory system.
/// </summary>
public class V1_InitialSchema : IMigration
{
    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public string Description => "Initial schema with vectors, keywords, links, cache, and quota tables";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        // Create schema_version table for version tracking
        await ExecuteNonQueryAsync(connection, @"
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL DEFAULT (datetime('now')),
                description TEXT
            )", cancellationToken);

        // Create vectors table for vector storage
        await ExecuteNonQueryAsync(connection, @"
            CREATE TABLE IF NOT EXISTS vectors (
                id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                embedding BLOB,
                metadata TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )", cancellationToken);

        // Create index on vectors.created_at
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_vectors_created_at ON vectors(created_at)", cancellationToken);

        // Create keywords table for FTS5 full-text search
        await ExecuteNonQueryAsync(connection, @"
            CREATE VIRTUAL TABLE IF NOT EXISTS keywords USING fts5(
                vector_id,
                keyword,
                content='vectors',
                content_rowid='rowid',
                tokenize='porter unicode61'
            )", cancellationToken);

        // Create links table for knowledge graph relationships
        await ExecuteNonQueryAsync(connection, @"
            CREATE TABLE IF NOT EXISTS links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                relation_type TEXT NOT NULL,
                weight REAL DEFAULT 1.0,
                metadata TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(source_id, target_id, relation_type)
            )", cancellationToken);

        // Create indexes on links
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_links_source_id ON links(source_id)", cancellationToken);
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_links_target_id ON links(target_id)", cancellationToken);
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_links_relation_type ON links(relation_type)", cancellationToken);

        // Create embedding_cache table for caching embeddings
        await ExecuteNonQueryAsync(connection, @"
            CREATE TABLE IF NOT EXISTS embedding_cache (
                cache_key TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL,
                embedding BLOB NOT NULL,
                model TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                expires_at TEXT
            )", cancellationToken);

        // Create index on embedding_cache for lookups
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_content_hash ON embedding_cache(content_hash)", cancellationToken);
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_embedding_cache_expires_at ON embedding_cache(expires_at)", cancellationToken);

        // Create token_usage table for tracking token consumption
        await ExecuteNonQueryAsync(connection, @"
            CREATE TABLE IF NOT EXISTS token_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id TEXT NOT NULL,
                model TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                operation_type TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            )", cancellationToken);

        // Create indexes on token_usage
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_token_usage_conversation_id ON token_usage(conversation_id)", cancellationToken);
        await ExecuteNonQueryAsync(connection, @"
            CREATE INDEX IF NOT EXISTS idx_token_usage_created_at ON token_usage(created_at)", cancellationToken);

        // Create quota_state table for quota tracking
        await ExecuteNonQueryAsync(connection, @"
            CREATE TABLE IF NOT EXISTS quota_state (
                key TEXT PRIMARY KEY,
                current_value INTEGER NOT NULL DEFAULT 0,
                max_value INTEGER NOT NULL,
                period_start TEXT NOT NULL,
                period_end TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT (datetime('now'))
            )", cancellationToken);

        // Record migration
        await ExecuteNonQueryAsync(connection, @"
            INSERT INTO schema_version (version, description)
            VALUES (1, 'Initial schema')
            ON CONFLICT(version) DO NOTHING", cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
