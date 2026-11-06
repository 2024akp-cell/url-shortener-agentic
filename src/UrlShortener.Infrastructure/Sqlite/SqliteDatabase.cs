using Microsoft.Data.Sqlite;

namespace UrlShortener.Infrastructure.Sqlite;

/// <summary>Owns the connection string and idempotent schema creation.</summary>
public sealed class SqliteDatabase
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;

    public SqliteDatabase(string connectionString) => _connectionString = connectionString;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public void EnsureCreated()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS links (
                code         TEXT    NOT NULL PRIMARY KEY COLLATE BINARY,
                target_url   TEXT    NOT NULL,
                created_at   INTEGER NOT NULL,
                expires_at   INTEGER NULL,
                is_custom    INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS clicks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                code         TEXT    NOT NULL,
                occurred_at  INTEGER NOT NULL,
                referrer     TEXT    NULL,
                user_agent   TEXT    NULL,
                client_hash  TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS ix_clicks_code_time ON clicks (code, occurred_at);
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            INSERT INTO schema_info (version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        command.ExecuteNonQuery();
    }

    internal static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
