using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkBookmark.Core;

namespace WorkBookmark.Storage;

/// <summary>One local writer. Every successful mutation has committed before it returns.</summary>
public sealed class SqliteBookmarkRepository : IBookmarkRepository
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    private readonly Action<string>? _beforeCommit;
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _disposed;
    private const int SchemaVersion = 1;
    private const string Columns = "id,kind,path,normalized_path,sheet_name,cell_address,display_name,note,created_at_utc,captured_at_utc,capture_sequence,note_updated_at_utc,had_unsaved_changes,last_resume_at_utc,last_resume_result,deleted_at_utc";

    // Hooks are local test injection only; product construction supplies just databasePath.
    public SqliteBookmarkRepository(string databasePath, Action<string>? beforeCommit = null, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) throw new BookmarkException(ResultCode.PersistenceFailed);
        _beforeCommit = beforeCommit;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 3 }.ToString();
        bool existed = File.Exists(fullPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // An existing empty/truncated file is evidence to preserve, not a new database.
            if (existed && new FileInfo(fullPath).Length == 0) throw new BookmarkException(ResultCode.PersistenceFailed);
            using var db = Open();
            using (var check = Command(db, null, "PRAGMA quick_check"))
            {
                if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal)) throw new BookmarkException(ResultCode.PersistenceFailed);
            }
            int version;
            using (var readVersion = Command(db, null, "PRAGMA user_version")) version = Convert.ToInt32(readVersion.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version > SchemaVersion || version < 0) throw new BookmarkException(ResultCode.PersistenceFailed);
            if (version == SchemaVersion)
            {
                using var verify = Command(db, null, "SELECT " + Columns + " FROM bookmarks LIMIT 0");
                using var reader = verify.ExecuteReader();
                return;
            }
            // v0 is only a truly empty SQLite schema. Unknown tables are never overwritten.
            using (var tables = Command(db, null, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"))
                if (Convert.ToInt64(tables.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new BookmarkException(ResultCode.PersistenceFailed);
            if (existed)
            {
                string backupPath = fullPath + ".pre-migration-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".bak";
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString());
                backup.Open();
                db.BackupDatabase(backup);
            }
            using var tx = db.BeginTransaction();
            using (var create = Command(db, tx, @"
CREATE TABLE bookmarks (
 id TEXT PRIMARY KEY NOT NULL,
 kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
 path TEXT NOT NULL,
 normalized_path TEXT COLLATE BINARY NOT NULL,
 sheet_name TEXT COLLATE BINARY,
 cell_address TEXT COLLATE BINARY,
 display_name TEXT NOT NULL,
 note TEXT NOT NULL DEFAULT '' CHECK(length(note)<=500),
 created_at_utc TEXT NOT NULL,
 captured_at_utc TEXT NOT NULL,
 capture_sequence INTEGER NOT NULL CHECK(capture_sequence>0),
 note_updated_at_utc TEXT,
 had_unsaved_changes INTEGER CHECK(had_unsaved_changes IS NULL OR had_unsaved_changes IN (0,1)),
 last_resume_at_utc TEXT,
 last_resume_result INTEGER,
 deleted_at_utc TEXT,
 CHECK((kind=2 AND sheet_name IS NOT NULL AND cell_address IS NOT NULL AND had_unsaved_changes IS NOT NULL) OR
       (kind<>2 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL))
);
CREATE UNIQUE INDEX bookmark_target ON bookmarks(kind, normalized_path, ifnull(sheet_name,''), ifnull(cell_address,''));
CREATE INDEX bookmark_recent ON bookmarks(deleted_at_utc,capture_sequence DESC);
CREATE TABLE metadata (name TEXT PRIMARY KEY NOT NULL,value INTEGER NOT NULL);
INSERT INTO metadata(name,value) VALUES('capture_sequence',0);
PRAGMA user_version=1;")) create.ExecuteNonQuery();
            _beforeCommit?.Invoke("migration");
            tx.Commit();
        }
        catch (BookmarkException) { throw; }
        catch { throw new BookmarkException(ResultCode.PersistenceFailed); }
    }

    public CaptureCommit UpsertCapture(CapturedTarget target)
    {
        target = PathPolicy.Validate(target);
        string normalized = PathPolicy.Normalize(target.Path);
        return Write("capture", (db, tx) =>
        {
            Bookmark? old = FindTarget(db, tx, target, normalized);
            long sequence;
            using (var current = Command(db, tx, "SELECT value FROM metadata WHERE name='capture_sequence'"))
                sequence = checked(Convert.ToInt64(current.ExecuteScalar(), CultureInfo.InvariantCulture) + 1);
            using (var advance = Command(db, tx, "UPDATE metadata SET value=$value WHERE name='capture_sequence'", ("$value", sequence)))
                if (advance.ExecuteNonQuery() != 1) throw new BookmarkException(ResultCode.PersistenceFailed);
            string now = Timestamp();
            Guid id = old?.Id ?? Guid.NewGuid();
            if (old is null)
            {
                using var insert = Command(db, tx, @"INSERT INTO bookmarks
(id,kind,path,normalized_path,sheet_name,cell_address,display_name,note,created_at_utc,captured_at_utc,capture_sequence,had_unsaved_changes)
VALUES($id,$kind,$path,$normalized,$sheet,$cell,$display,'',$now,$now,$sequence,$unsaved)",
                    ("$id", id.ToString()), ("$kind", (int)target.Kind), ("$path", target.Path), ("$normalized", normalized),
                    ("$sheet", target.SheetName), ("$cell", target.CellAddress), ("$display", PathPolicy.DisplayName(target.Path)),
                    ("$now", now), ("$sequence", sequence), ("$unsaved", target.HadUnsavedChanges));
                insert.ExecuteNonQuery();
            }
            else
            {
                using var update = Command(db, tx, @"UPDATE bookmarks SET path=$path,display_name=$display,captured_at_utc=$now,
 capture_sequence=$sequence,had_unsaved_changes=$unsaved,deleted_at_utc=NULL WHERE id=$id",
                    ("$path", target.Path), ("$display", PathPolicy.DisplayName(target.Path)), ("$now", now),
                    ("$sequence", sequence), ("$unsaved", target.HadUnsavedChanges), ("$id", id.ToString()));
                update.ExecuteNonQuery();
            }
            return new CaptureCommit(FindId(db, tx, id)!, !string.IsNullOrEmpty(old?.Note), old?.DeletedAtUtc != null);
        });
    }

    public SearchResults List(string query = "") => Read(db =>
    {
        query ??= "";
        int limit = string.IsNullOrWhiteSpace(query) ? 20 : 100;
        string filter = string.IsNullOrWhiteSpace(query) ? "" : @" AND (
 instr(lower(display_name),lower($query))>0 OR instr(lower(path),lower($query))>0 OR
 instr(lower(ifnull(sheet_name,'')),lower($query))>0 OR instr(lower(note),lower($query))>0)";
        using var command = Command(db, null, "SELECT " + Columns + " FROM bookmarks WHERE deleted_at_utc IS NULL" + filter + " ORDER BY capture_sequence DESC LIMIT $limit", ("$limit", limit + 1));
        if (filter.Length != 0) command.Parameters.AddWithValue("$query", query);
        using var reader = command.ExecuteReader();
        var results = new List<Bookmark>();
        while (reader.Read()) results.Add(Materialize(reader));
        bool more = results.Count > limit;
        if (more) results.RemoveAt(results.Count - 1);
        return new SearchResults(results, more);
    });

    public Bookmark? Get(Guid id) => Read(db => FindId(db, null, id));

    public void UpdateNote(Guid id, string note)
    {
        if (note == null || note.Length > 500 || note.Any(c => c is '\r' or '\n' or '\0' or '\u0085' or '\u2028' or '\u2029'))
            throw new BookmarkException(ResultCode.InvalidRequest);
        Write("note", (db, tx) => ExecuteExisting(db, tx, id,
            "UPDATE bookmarks SET note=$note,note_updated_at_utc=$now WHERE id=$id", ("$note", note), ("$now", Timestamp())));
    }

    public void SoftDelete(Guid id) => Write("delete", (db, tx) => ExecuteExisting(db, tx, id,
        "UPDATE bookmarks SET deleted_at_utc=$now WHERE id=$id", ("$now", Timestamp())));

    public void Restore(Guid id) => Write("restore", (db, tx) => ExecuteExisting(db, tx, id,
        "UPDATE bookmarks SET deleted_at_utc=NULL WHERE id=$id"));

    public void RecordResume(Guid id, ResultCode result)
    {
        if (!Enum.IsDefined(result)) throw new BookmarkException(ResultCode.InvalidRequest);
        Write("resume", (db, tx) => ExecuteExisting(db, tx, id,
            "UPDATE bookmarks SET last_resume_at_utc=$now,last_resume_result=$result WHERE id=$id", ("$now", Timestamp()), ("$result", (int)result)));
    }

    public void Relink(Guid id, CapturedTarget validatedTarget)
    {
        PathPolicy.Validate(validatedTarget);
        string normalized = PathPolicy.Normalize(validatedTarget.Path);
        Write("relink", (db, tx) =>
        {
            Bookmark old = FindId(db, tx, id) ?? throw new BookmarkException(ResultCode.TargetUnavailable);
            if (old.Target.Kind != validatedTarget.Kind || !string.Equals(old.Target.SheetName, validatedTarget.SheetName, StringComparison.Ordinal)
                || !string.Equals(old.Target.CellAddress, validatedTarget.CellAddress, StringComparison.Ordinal)) throw new BookmarkException(ResultCode.InvalidRequest);
            Bookmark? duplicate = FindTarget(db, tx, validatedTarget, normalized);
            if (duplicate != null && duplicate.Id != id) throw new BookmarkException(ResultCode.DuplicateTarget);
            return ExecuteExisting(db, tx, id, "UPDATE bookmarks SET path=$path,normalized_path=$normalized,display_name=$display WHERE id=$id",
                ("$path", validatedTarget.Path), ("$normalized", normalized), ("$display", PathPolicy.DisplayName(validatedTarget.Path)));
        });
    }

    public void Dispose() { lock (_gate) _disposed = true; }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        try
        {
            db.Open();
            using var command = Command(db, null, "PRAGMA busy_timeout=3000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
            command.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private T Read<T>(Func<SqliteConnection, T> read)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { using var db = Open(); return read(db); }
            catch (BookmarkException) { throw; }
            catch { throw new BookmarkException(ResultCode.PersistenceFailed); }
        }
    }

    private T Write<T>(string operation, Func<SqliteConnection, SqliteTransaction, T> action) => Read(db =>
    {
        using var tx = db.BeginTransaction();
        T result = action(db, tx);
        _beforeCommit?.Invoke(operation);
        tx.Commit();
        return result;
    });

    private string Timestamp() => _utcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int ExecuteExisting(SqliteConnection db, SqliteTransaction tx, Guid id, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        command.Parameters.AddWithValue("$id", id.ToString());
        int count = command.ExecuteNonQuery();
        if (count != 1) throw new BookmarkException(ResultCode.TargetUnavailable);
        return count;
    }

    private static Bookmark? FindId(SqliteConnection db, SqliteTransaction? tx, Guid id)
    {
        using var command = Command(db, tx, "SELECT " + Columns + " FROM bookmarks WHERE id=$id", ("$id", id.ToString()));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Materialize(reader) : null;
    }

    private static Bookmark? FindTarget(SqliteConnection db, SqliteTransaction tx, CapturedTarget target, string normalized)
    {
        using var command = Command(db, tx, "SELECT " + Columns + " FROM bookmarks WHERE kind=$kind AND normalized_path=$path AND ifnull(sheet_name,'')=$sheet AND ifnull(cell_address,'')=$cell",
            ("$kind", (int)target.Kind), ("$path", normalized), ("$sheet", target.SheetName ?? ""), ("$cell", target.CellAddress ?? ""));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Materialize(reader) : null;
    }

    private static Bookmark Materialize(SqliteDataReader row) => new(
        Guid.Parse(row.GetString(0)),
        new CapturedTarget((TargetKind)row.GetInt32(1), row.GetString(2), StringOrNull(row, 4), StringOrNull(row, 5), row.IsDBNull(12) ? null : row.GetBoolean(12)),
        row.GetString(3), row.GetString(6), row.GetString(7), DateTimeOffset.Parse(row.GetString(8), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(row.GetString(9), CultureInfo.InvariantCulture), row.GetInt64(10), DateOrNull(row, 11), DateOrNull(row, 13),
        row.IsDBNull(14) ? null : (ResultCode)row.GetInt32(14), DateOrNull(row, 15));

    private static string? StringOrNull(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private static DateTimeOffset? DateOrNull(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : DateTimeOffset.Parse(row.GetString(index), CultureInfo.InvariantCulture);
}
