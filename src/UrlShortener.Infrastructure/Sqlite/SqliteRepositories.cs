using Microsoft.Data.Sqlite;
using UrlShortener.Core.Abstractions;
using UrlShortener.Core.Domain;

namespace UrlShortener.Infrastructure.Sqlite;

public sealed class SqliteLinkRepository : ILinkRepository
{
    private const int SqliteConstraintViolation = 19;

    private readonly SqliteDatabase _db;

    public SqliteLinkRepository(SqliteDatabase db) => _db = db;

    public async Task<bool> TryAddAsync(ShortLink link, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO links (code, target_url, created_at, expires_at, is_custom)
            VALUES ($code, $url, $created, $expires, $custom)
            """;
        command.Parameters.AddWithValue("$code", link.Code);
        command.Parameters.AddWithValue("$url", link.TargetUrl);
        command.Parameters.AddWithValue("$created", SqliteDatabase.ToUnixMs(link.CreatedAt));
        command.Parameters.AddWithValue("$expires", link.ExpiresAt is { } e ? SqliteDatabase.ToUnixMs(e) : DBNull.Value);
        command.Parameters.AddWithValue("$custom", link.IsCustomAlias ? 1 : 0);
        try
        {
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintViolation)
        {
            return false;
        }
    }

    public async Task<ShortLink?> GetAsync(string code, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT code, target_url, created_at, expires_at, is_custom FROM links WHERE code = $code";
        command.Parameters.AddWithValue("$code", code);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new ShortLink(
            reader.GetString(0),
            reader.GetString(1),
            SqliteDatabase.FromUnixMs(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : SqliteDatabase.FromUnixMs(reader.GetInt64(3)),
            reader.GetInt64(4) == 1);
    }

    public async Task<bool> DeleteAsync(string code, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM links WHERE code = $code";
        command.Parameters.AddWithValue("$code", code);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }
}

public sealed class SqliteClickRepository : IClickRepository
{
    private readonly SqliteDatabase _db;

    public SqliteClickRepository(SqliteDatabase db) => _db = db;

    public async Task AddAsync(ClickEvent click, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO clicks (code, occurred_at, referrer, user_agent, client_hash)
            VALUES ($code, $at, $ref, $ua, $hash)
            """;
        command.Parameters.AddWithValue("$code", click.Code);
        command.Parameters.AddWithValue("$at", SqliteDatabase.ToUnixMs(click.OccurredAt));
        command.Parameters.AddWithValue("$ref", (object?)click.Referrer ?? DBNull.Value);
        command.Parameters.AddWithValue("$ua", (object?)click.UserAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)click.ClientHash ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<LinkStats> GetStatsAsync(string code, DateTimeOffset since, int topReferrers, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);

        long total, unique;
        DateTimeOffset? last;
        await using (var summary = connection.CreateCommand())
        {
            summary.CommandText = """
                SELECT COUNT(*), COUNT(DISTINCT client_hash), MAX(occurred_at)
                FROM clicks WHERE code = $code
                """;
            summary.Parameters.AddWithValue("$code", code);
            await using var reader = await summary.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            total = reader.GetInt64(0);
            unique = reader.GetInt64(1);
            last = reader.IsDBNull(2) ? null : SqliteDatabase.FromUnixMs(reader.GetInt64(2));
        }

        var daily = new List<DailyClicks>();
        await using (var byDay = connection.CreateCommand())
        {
            byDay.CommandText = """
                SELECT date(occurred_at / 1000, 'unixepoch') AS day, COUNT(*)
                FROM clicks WHERE code = $code AND occurred_at >= $since
                GROUP BY day ORDER BY day
                """;
            byDay.Parameters.AddWithValue("$code", code);
            byDay.Parameters.AddWithValue("$since", SqliteDatabase.ToUnixMs(since));
            await using var reader = await byDay.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                daily.Add(new DailyClicks(DateOnly.Parse(reader.GetString(0)), reader.GetInt64(1)));
        }

        var referrers = new List<ReferrerCount>();
        await using (var top = connection.CreateCommand())
        {
            top.CommandText = """
                SELECT referrer, COUNT(*) AS n
                FROM clicks WHERE code = $code AND occurred_at >= $since AND referrer IS NOT NULL AND referrer <> ''
                GROUP BY referrer ORDER BY n DESC, referrer LIMIT $limit
                """;
            top.Parameters.AddWithValue("$code", code);
            top.Parameters.AddWithValue("$since", SqliteDatabase.ToUnixMs(since));
            top.Parameters.AddWithValue("$limit", topReferrers);
            await using var reader = await top.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                referrers.Add(new ReferrerCount(reader.GetString(0), reader.GetInt64(1)));
        }

        return new LinkStats(code, total, unique, last, daily, referrers);
    }

    public async Task DeleteForCodeAsync(string code, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM clicks WHERE code = $code";
        command.Parameters.AddWithValue("$code", code);
        await command.ExecuteNonQueryAsync(ct);
    }
}
