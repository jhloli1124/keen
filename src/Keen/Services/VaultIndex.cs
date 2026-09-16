using Keen.Models;
using Microsoft.Data.Sqlite;

namespace Keen.Services;

// SQLite 索引(不变量⑤⑨):单连接 + SemaphoreSlim(1,1) 串行所有访问,规避多写者 WAL-reset 前置条件。
// 连接串只用 Data Source + Default Timeout;journal_mode/WAL 等经打开后 PRAGMA 设置(关键词写法已驳反)。
// 前向迁移靠 user_version。
internal sealed class VaultIndex : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public VaultIndex(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath};Default Timeout=30;");
        _conn.Open();

        using (var c = _conn.CreateCommand())
        {
            c.CommandText = "PRAGMA journal_mode=WAL;";
            c.ExecuteScalar(); // 持久写入 DB 头
        }
        using (var c = _conn.CreateCommand())
        {
            c.CommandText =
                "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
            c.ExecuteNonQuery();
        }
        Migrate();
    }

    private long UserVersion()
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "PRAGMA user_version;";
        return (long)c.ExecuteScalar()!;
    }

    private void SetUserVersion(long v)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = $"PRAGMA user_version = {v};";
        c.ExecuteNonQuery();
    }

    private void Migrate()
    {
        if (UserVersion() < 1)
        {
            using var tx = _conn.BeginTransaction();
            var c = _conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = """
                CREATE TABLE IF NOT EXISTS watched_file(
                  guid TEXT PRIMARY KEY,
                  added_at_ticks INTEGER NOT NULL,
                  current_path TEXT NOT NULL,
                  display_name TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 1,
                  note TEXT);
                CREATE TABLE IF NOT EXISTS version(
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  watched_guid TEXT NOT NULL REFERENCES watched_file(guid),
                  captured_at_ticks INTEGER NOT NULL,
                  seq INTEGER NOT NULL,
                  kind INTEGER NOT NULL DEFAULT 0,
                  stored_relpath TEXT NOT NULL,
                  orig_path_at_capture TEXT NOT NULL,
                  orig_filename TEXT NOT NULL,
                  size_bytes INTEGER NOT NULL,
                  sha256 TEXT,
                  note TEXT,
                  UNIQUE(watched_guid, captured_at_ticks, seq));
                CREATE INDEX IF NOT EXISTS idx_version_guid_time ON version(watched_guid, captured_at_ticks DESC);
                CREATE INDEX IF NOT EXISTS idx_version_sha ON version(sha256);
                """;
            c.ExecuteNonQuery();
            tx.Commit();
            SetUserVersion(1);
        }
    }

    public async Task<List<WatchedFile>> LoadActiveWatchedFilesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<WatchedFile>();
            using var c = _conn.CreateCommand();
            c.CommandText =
                "SELECT guid, added_at_ticks, current_path, display_name, is_active, note FROM watched_file WHERE is_active=1;";
            using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(
                    new WatchedFile
                    {
                        Guid = Guid.Parse(r.GetString(0)),
                        AddedAtTicks = r.GetInt64(1),
                        CurrentPath = r.GetString(2),
                        DisplayName = r.GetString(3),
                        IsActive = r.GetBoolean(4),
                        Note = r.IsDBNull(5) ? null : r.GetString(5),
                    }
                );
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<WatchedFile>> LoadAllWatchedFilesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<WatchedFile>();
            using var c = _conn.CreateCommand();
            c.CommandText =
                "SELECT guid, added_at_ticks, current_path, display_name, is_active, note FROM watched_file ORDER BY is_active DESC, display_name COLLATE NOCASE;";
            using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(
                    new WatchedFile
                    {
                        Guid = Guid.Parse(r.GetString(0)),
                        AddedAtTicks = r.GetInt64(1),
                        CurrentPath = r.GetString(2),
                        DisplayName = r.GetString(3),
                        IsActive = r.GetBoolean(4),
                        Note = r.IsDBNull(5) ? null : r.GetString(5),
                    }
                );
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WatchedFile?> GetWatchedFileAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText =
                "SELECT guid, added_at_ticks, current_path, display_name, is_active, note FROM watched_file WHERE guid=@g;";
            c.Parameters.AddWithValue("@g", guid.ToString());
            using var r = await c.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                return new WatchedFile
                {
                    Guid = Guid.Parse(r.GetString(0)),
                    AddedAtTicks = r.GetInt64(1),
                    CurrentPath = r.GetString(2),
                    DisplayName = r.GetString(3),
                    IsActive = r.GetBoolean(4),
                    Note = r.IsDBNull(5) ? null : r.GetString(5),
                };
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ReactivateWatchedFileAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE watched_file SET is_active=1 WHERE guid=@g;";
            c.Parameters.AddWithValue("@g", guid.ToString());
            await c.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    // 彻底删除:返回 version 的 stored_relpath 供调用方删 blob,再删 version 行 + watched_file 行。
    public async Task<List<string>> PurgeWatchedFileAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            var rels = new List<string>();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "SELECT stored_relpath FROM version WHERE watched_guid=@g;";
                c.Parameters.AddWithValue("@g", guid.ToString());
                using var r = await c.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    rels.Add(r.GetString(0));
            }
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "DELETE FROM version WHERE watched_guid=@g;";
                c.Parameters.AddWithValue("@g", guid.ToString());
                await c.ExecuteNonQueryAsync();
            }
            using (var c = _conn.CreateCommand())
            {
                c.CommandText = "DELETE FROM watched_file WHERE guid=@g;";
                c.Parameters.AddWithValue("@g", guid.ToString());
                await c.ExecuteNonQueryAsync();
            }
            return rels;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddWatchedFileAsync(WatchedFile wf)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText =
                @"INSERT INTO watched_file(guid, added_at_ticks, current_path, display_name, is_active, note)
                              VALUES(@g,@a,@p,@n,1,@note);";
            c.Parameters.AddWithValue("@g", wf.Guid.ToString());
            c.Parameters.AddWithValue("@a", wf.AddedAtTicks);
            c.Parameters.AddWithValue("@p", wf.CurrentPath);
            c.Parameters.AddWithValue("@n", wf.DisplayName);
            c.Parameters.AddWithValue("@note", (object?)wf.Note ?? DBNull.Value);
            await c.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeactivateWatchedFileAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE watched_file SET is_active=0 WHERE guid=@g;";
            c.Parameters.AddWithValue("@g", guid.ToString());
            await c.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateWatchedFilePathAsync(Guid guid, string newPath, string displayName)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText =
                "UPDATE watched_file SET current_path=@p, display_name=@n WHERE guid=@g;";
            c.Parameters.AddWithValue("@p", newPath);
            c.Parameters.AddWithValue("@n", displayName);
            c.Parameters.AddWithValue("@g", guid.ToString());
            await c.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> CountVersionsAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*) FROM version WHERE watched_guid=@g;";
            c.Parameters.AddWithValue("@g", guid.ToString());
            return (long)(await c.ExecuteScalarAsync())!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> GetTotalSizeAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText =
                "SELECT COALESCE(SUM(size_bytes),0) FROM version WHERE watched_guid=@g;";
            c.Parameters.AddWithValue("@g", guid.ToString());
            return (long)(await c.ExecuteScalarAsync())!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(long count, long size)> GetTotalsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*), COALESCE(SUM(size_bytes),0) FROM version;";
            using var r = await c.ExecuteReaderAsync();
            if (await r.ReadAsync())
                return (r.GetInt64(0), r.GetInt64(1));
            return (0, 0);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<VersionEntry?> GetLastVersionAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText =
                "SELECT id, captured_at_ticks, seq, kind, stored_relpath, orig_path_at_capture, orig_filename, size_bytes, sha256 FROM version WHERE watched_guid=@g ORDER BY captured_at_ticks DESC, seq DESC LIMIT 1;";
            c.Parameters.AddWithValue("@g", guid.ToString());
            using var r = await c.ExecuteReaderAsync();
            if (await r.ReadAsync())
                return ReadVersion(r, guid);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<VersionEntry>> GetVersionsAsync(Guid guid)
    {
        await _lock.WaitAsync();
        try
        {
            var list = new List<VersionEntry>();
            using var c = _conn.CreateCommand();
            c.CommandText =
                "SELECT id, captured_at_ticks, seq, kind, stored_relpath, orig_path_at_capture, orig_filename, size_bytes, sha256 FROM version WHERE watched_guid=@g ORDER BY captured_at_ticks DESC, seq DESC;";
            c.Parameters.AddWithValue("@g", guid.ToString());
            using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(ReadVersion(r, guid));
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<VersionEntry> InsertVersionAsync(
        Guid guid,
        long capturedAtTicks,
        int seq,
        VersionKind kind,
        string relPath,
        string origPath,
        string origName,
        long size,
        string? sha
    )
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText =
                @"INSERT INTO version(watched_guid, captured_at_ticks, seq, kind, stored_relpath, orig_path_at_capture, orig_filename, size_bytes, sha256)
                              VALUES(@g,@t,@s,@k,@r,@op,@on,@sz,@h);
                              SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("@g", guid.ToString());
            c.Parameters.AddWithValue("@t", capturedAtTicks);
            c.Parameters.AddWithValue("@s", seq);
            c.Parameters.AddWithValue("@k", (int)kind);
            c.Parameters.AddWithValue("@r", relPath);
            c.Parameters.AddWithValue("@op", origPath);
            c.Parameters.AddWithValue("@on", origName);
            c.Parameters.AddWithValue("@sz", size);
            c.Parameters.AddWithValue("@h", (object?)sha ?? DBNull.Value);
            var id = (long)(await c.ExecuteScalarAsync())!;
            return new VersionEntry
            {
                Id = id,
                WatchedGuid = guid,
                CapturedAtTicks = capturedAtTicks,
                Seq = seq,
                Kind = kind,
                StoredRelPath = relPath,
                OrigPathAtCapture = origPath,
                OrigFilename = origName,
                SizeBytes = size,
                Sha256 = sha,
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    // 保留策略清理:超过 keepLast 份或早于 maxAgeDays 的版本删行,返回其 stored_relpath 供调用方删 blob。
    // keepLast<=0 表示不限数量;maxAgeDays<=0 表示不限时间。
    public async Task<List<string>> PruneAsync(Guid guid, int keepLast, int maxAgeDays)
    {
        await _lock.WaitAsync();
        try
        {
            var rows = new List<(long id, long ticks, string rel)>();
            using (var c = _conn.CreateCommand())
            {
                c.CommandText =
                    "SELECT id, captured_at_ticks, stored_relpath FROM version WHERE watched_guid=@g ORDER BY captured_at_ticks DESC, seq DESC;";
                c.Parameters.AddWithValue("@g", guid.ToString());
                using var r = await c.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
            }

            long cutoff = maxAgeDays > 0 ? DateTime.UtcNow.AddDays(-maxAgeDays).Ticks : 0;
            var drop = new List<(long id, string rel)>();
            for (int i = 0; i < rows.Count; i++)
            {
                bool tooMany = keepLast > 0 && i >= keepLast;
                bool tooOld = maxAgeDays > 0 && rows[i].ticks < cutoff;
                if (tooMany || tooOld)
                    drop.Add((rows[i].id, rows[i].rel));
            }
            if (drop.Count == 0)
                return new List<string>();

            foreach (var (id, _) in drop)
            {
                using var c = _conn.CreateCommand();
                c.CommandText = "DELETE FROM version WHERE id=@id;";
                c.Parameters.AddWithValue("@id", id);
                await c.ExecuteNonQueryAsync();
            }
            return drop.Select(t => t.rel).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetVersionNoteAsync(long id, string? note)
    {
        await _lock.WaitAsync();
        try
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "UPDATE version SET note=@n WHERE id=@id;";
            c.Parameters.AddWithValue("@n", (object?)note ?? DBNull.Value);
            c.Parameters.AddWithValue("@id", id);
            await c.ExecuteNonQueryAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static VersionEntry ReadVersion(SqliteDataReader r, Guid guid) =>
        new()
        {
            Id = r.GetInt64(0),
            WatchedGuid = guid,
            CapturedAtTicks = r.GetInt64(1),
            Seq = r.GetInt32(2),
            Kind = (VersionKind)r.GetInt32(3),
            StoredRelPath = r.GetString(4),
            OrigPathAtCapture = r.GetString(5),
            OrigFilename = r.GetString(6),
            SizeBytes = r.GetInt64(7),
            Sha256 = r.IsDBNull(8) ? null : r.GetString(8),
        };

    public void Dispose()
    {
        _conn.Dispose();
        _lock.Dispose();
    }
}
