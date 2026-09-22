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
    private const int SchemaVersion = 5;
    private const string LegacyColumns = "id,kind,path,normalized_path,sheet_name,cell_address,display_name,note,created_at_utc,captured_at_utc,capture_sequence,note_updated_at_utc,had_unsaved_changes,last_resume_at_utc,last_resume_result,deleted_at_utc";

    private const string VersionTwoColumns = LegacyColumns + ",word_start,slide_id,slide_number,pdf_page,page_title";
    private const string VersionThreeColumns = VersionTwoColumns + ",text_offset";
    private const string Columns = VersionThreeColumns + ",text_content,text_selection_end,snapshot_title";
    private const string StickerColumns = "bookmark_id,monitor_device,left_offset,top_offset,width,height,is_collapsed,always_on_top";

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
                foreach (string sql in new[] { "SELECT " + Columns + " FROM bookmarks LIMIT 0", "SELECT " + StickerColumns + " FROM sticker_layouts LIMIT 0" })
                {
                    using var verify = Command(db, null, sql);
                    using var reader = verify.ExecuteReader();
                }
                return;
            }
            if (version == 0)
            {
                // Only a truly empty v0 schema may be initialized. Unknown user tables are preserved.
                using var tables = Command(db, null, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
                if (Convert.ToInt64(tables.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new BookmarkException(ResultCode.PersistenceFailed);
            }
            else
            {
                using var verify = Command(db, null, "SELECT " + (version == 1 ? LegacyColumns : version == 2 ? VersionTwoColumns : version == 3 ? VersionThreeColumns : Columns) + " FROM bookmarks LIMIT 0");
                using var reader = verify.ExecuteReader();
            }
            if (existed)
            {
                string backupPath = fullPath + ".pre-migration-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".bak";
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString());
                backup.Open();
                db.BackupDatabase(backup);
            }
            using var tx = db.BeginTransaction();
            if (version is 1 or 2 or 3)
            {
                using var rename = Command(db, tx, "ALTER TABLE bookmarks RENAME TO bookmarks_previous; DROP INDEX bookmark_target; DROP INDEX bookmark_recent;");
                rename.ExecuteNonQuery();
            }
            // Version 4 already has the complete bookmark schema. Add presentation state without rebuilding its table.
            if (version < 4)
                using (var create = Command(db, tx, SchemaSql)) create.ExecuteNonQuery();
            if (version is 1 or 2 or 3)
            {
                var preservedColumns = version == 1 ? LegacyColumns : version == 2 ? VersionTwoColumns : VersionThreeColumns;
                using var copy = Command(db, tx, "INSERT INTO bookmarks (" + preservedColumns + ") SELECT " + preservedColumns + " FROM bookmarks_previous; DROP TABLE bookmarks_previous;");
                copy.ExecuteNonQuery();
            }
            else if (version == 0)
            {
                using var metadata = Command(db, tx, "CREATE TABLE metadata (name TEXT PRIMARY KEY NOT NULL,value INTEGER NOT NULL); INSERT INTO metadata(name,value) VALUES('capture_sequence',0);");
                metadata.ExecuteNonQuery();
            }
            using (var createStickers = Command(db, tx, StickerSchemaSql)) createStickers.ExecuteNonQuery();
            using (var setVersion = Command(db, tx, "PRAGMA user_version=5;")) setVersion.ExecuteNonQuery();
            _beforeCommit?.Invoke("migration");
            tx.Commit();
        }
        catch (BookmarkException) { throw; }
        catch { throw new BookmarkException(ResultCode.PersistenceFailed); }
    }

    private const string SchemaSql = @"
CREATE TABLE bookmarks (
 id TEXT PRIMARY KEY NOT NULL,
 kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 8),
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
 word_start INTEGER CHECK(word_start>=0),
 slide_id INTEGER CHECK(slide_id>0),
 slide_number INTEGER CHECK(slide_number>0),
 pdf_page INTEGER CHECK(pdf_page>0),
 page_title TEXT CHECK(length(page_title)<=256),
 text_offset INTEGER CHECK(text_offset>=0),
 text_content TEXT CHECK(text_content IS NULL OR (length(text_content)<=2097152 AND instr(text_content,char(0))=0 AND instr(text_content,char(13))=0)),
 text_selection_end INTEGER CHECK(text_selection_end>=0 AND text_selection_end<=2097152),
 snapshot_title TEXT CHECK(length(snapshot_title) BETWEEN 1 AND 256),
 CHECK((kind IN (7,8) AND text_offset IS NOT NULL) OR (kind NOT IN (7,8) AND text_offset IS NULL)),
 CHECK((kind=8 AND text_content IS NOT NULL AND text_selection_end IS NOT NULL AND snapshot_title IS NOT NULL AND text_offset<=text_selection_end
        AND length(path)=81 AND substr(path,1,17)='notepad-snapshot:' AND substr(path,18) NOT GLOB '*[^0-9A-F]*' AND normalized_path=path) OR
       (kind<>8 AND text_content IS NULL AND text_selection_end IS NULL AND snapshot_title IS NULL)),
 CHECK((kind=6 AND page_title IS NOT NULL) OR (kind<>6 AND page_title IS NULL)),
 CHECK((kind IN (0,1) AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=2 AND sheet_name IS NOT NULL AND cell_address IS NOT NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=3 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NOT NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind=4 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NOT NULL AND slide_number IS NOT NULL AND pdf_page IS NULL) OR
       (kind=5 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NOT NULL) OR
       (kind=6 AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL) OR
       (kind IN (7,8) AND sheet_name IS NULL AND cell_address IS NULL AND had_unsaved_changes IS NOT NULL AND word_start IS NULL AND slide_id IS NULL AND slide_number IS NULL AND pdf_page IS NULL))
);
CREATE UNIQUE INDEX bookmark_target ON bookmarks(kind, normalized_path, ifnull(sheet_name,''), ifnull(cell_address,''), ifnull(word_start,-1), ifnull(slide_id,-1), ifnull(pdf_page,-1), ifnull(text_offset,-1), ifnull(text_selection_end,-1));
CREATE INDEX bookmark_recent ON bookmarks(deleted_at_utc,capture_sequence DESC);";

    private const string StickerSchemaSql = @"
CREATE TABLE sticker_layouts (
 bookmark_id TEXT PRIMARY KEY NOT NULL REFERENCES bookmarks(id) ON DELETE CASCADE,
 monitor_device TEXT NOT NULL CHECK(length(monitor_device)<=256 AND instr(monitor_device,char(0))=0),
 left_offset INTEGER NOT NULL CHECK(left_offset BETWEEN -100000 AND 100000),
 top_offset INTEGER NOT NULL CHECK(top_offset BETWEEN -100000 AND 100000),
 width INTEGER NOT NULL CHECK(width BETWEEN 1 AND 16384),
 height INTEGER NOT NULL CHECK(height BETWEEN 1 AND 16384),
 is_collapsed INTEGER NOT NULL CHECK(is_collapsed IN (0,1)),
 always_on_top INTEGER NOT NULL CHECK(always_on_top IN (0,1))
);
CREATE INDEX bookmark_deleted ON bookmarks(deleted_at_utc DESC,capture_sequence DESC) WHERE deleted_at_utc IS NOT NULL;";

    public CaptureCommit UpsertCapture(CapturedTarget target)
    {
        target = PathPolicy.Validate(target);
        string normalized = PathPolicy.NormalizeLocation(target);
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
(id,kind,path,normalized_path,sheet_name,cell_address,display_name,note,created_at_utc,captured_at_utc,capture_sequence,had_unsaved_changes,word_start,slide_id,slide_number,pdf_page,page_title,text_offset,text_content,text_selection_end,snapshot_title)
VALUES($id,$kind,$path,$normalized,$sheet,$cell,$display,'',$now,$now,$sequence,$unsaved,$word,$slide,$slideNumber,$pdf,$title,$text,$content,$textEnd,$snapshotTitle)",
                    ("$id", id.ToString()), ("$kind", (int)target.Kind), ("$path", target.Path), ("$normalized", normalized),
                    ("$sheet", target.SheetName), ("$cell", target.CellAddress), ("$display", PathPolicy.DisplayName(target)),
                    ("$now", now), ("$sequence", sequence), ("$unsaved", target.HadUnsavedChanges),
                    ("$word", target.WordStart), ("$slide", target.SlideId), ("$slideNumber", target.SlideNumber), ("$pdf", target.PdfPage), ("$title", target.PageTitle), ("$text", target.TextOffset),
                    ("$content", target.TextContent), ("$textEnd", target.TextSelectionEnd), ("$snapshotTitle", target.SnapshotTitle));
                insert.ExecuteNonQuery();
            }
            else
            {
                using var update = Command(db, tx, @"UPDATE bookmarks SET path=$path,display_name=$display,captured_at_utc=$now,
 capture_sequence=$sequence,had_unsaved_changes=$unsaved,slide_number=$slideNumber,page_title=$title,deleted_at_utc=NULL WHERE id=$id",
                    ("$path", target.Path), ("$display", PathPolicy.DisplayName(target)), ("$now", now),
                    ("$sequence", sequence), ("$unsaved", target.HadUnsavedChanges), ("$slideNumber", target.SlideNumber), ("$title", target.PageTitle), ("$id", id.ToString()));
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

    public IReadOnlyList<Bookmark> ListActive() => Read(db =>
    {
        using var command = Command(db, null, "SELECT " + Columns + " FROM bookmarks WHERE deleted_at_utc IS NULL ORDER BY capture_sequence DESC");
        using var reader = command.ExecuteReader();
        var results = new List<Bookmark>();
        while (reader.Read()) results.Add(Materialize(reader));
        return results;
    });

    public IReadOnlyList<Bookmark> ListDeleted(int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new BookmarkException(ResultCode.InvalidRequest);
        return Read(db =>
        {
            using var command = Command(db, null, "SELECT " + Columns + " FROM bookmarks WHERE deleted_at_utc IS NOT NULL ORDER BY deleted_at_utc DESC,capture_sequence DESC LIMIT $limit", ("$limit", limit));
            using var reader = command.ExecuteReader();
            var results = new List<Bookmark>();
            while (reader.Read()) results.Add(Materialize(reader));
            return results;
        });
    }

    public IReadOnlyList<StickerLayout> GetStickerLayouts() => Read(db =>
    {
        using var command = Command(db, null, "SELECT " + StickerColumns + " FROM sticker_layouts ORDER BY bookmark_id");
        using var reader = command.ExecuteReader();
        var results = new List<StickerLayout>();
        while (reader.Read())
        {
            var layout = new StickerLayout(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetBoolean(6), reader.GetBoolean(7));
            ValidateStickerLayout(layout, ResultCode.PersistenceFailed);
            results.Add(layout);
        }
        return results;
    });

    public void SaveStickerLayout(StickerLayout layout)
    {
        ValidateStickerLayout(layout, ResultCode.InvalidRequest);
        Write("sticker-layout", (db, tx) =>
        {
            using (var exists = Command(db, tx, "SELECT 1 FROM bookmarks WHERE id=$id", ("$id", layout.BookmarkId.ToString())))
                if (exists.ExecuteScalar() is null) throw new BookmarkException(ResultCode.TargetUnavailable);
            using var command = Command(db, tx, @"INSERT INTO sticker_layouts (bookmark_id,monitor_device,left_offset,top_offset,width,height,is_collapsed,always_on_top)
VALUES($id,$monitor,$left,$top,$width,$height,$collapsed,$topmost)
ON CONFLICT(bookmark_id) DO UPDATE SET monitor_device=excluded.monitor_device,left_offset=excluded.left_offset,top_offset=excluded.top_offset,
width=excluded.width,height=excluded.height,is_collapsed=excluded.is_collapsed,always_on_top=excluded.always_on_top",
                ("$id", layout.BookmarkId.ToString()), ("$monitor", layout.MonitorDevice), ("$left", layout.Left), ("$top", layout.Top),
                ("$width", layout.Width), ("$height", layout.Height), ("$collapsed", layout.IsCollapsed), ("$topmost", layout.AlwaysOnTop));
            return command.ExecuteNonQuery();
        });
    }

    private static void ValidateStickerLayout(StickerLayout layout, ResultCode failure)
    {
        if (layout is null || layout.BookmarkId == Guid.Empty || layout.MonitorDevice is null || layout.MonitorDevice.Length > 256 || layout.MonitorDevice.Any(char.IsControl)
            || layout.Left is < -100000 or > 100000 || layout.Top is < -100000 or > 100000
            || layout.Width is < 1 or > 16384 || layout.Height is < 1 or > 16384)
            throw new BookmarkException(failure);
    }

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
        if (validatedTarget.Kind is TargetKind.WebPage or TargetKind.NotepadSnapshot || OfficeLocation.IsWebTarget(validatedTarget)) throw new BookmarkException(ResultCode.InvalidRequest);
        string normalized = PathPolicy.Normalize(validatedTarget.Path);
        Write("relink", (db, tx) =>
        {
            Bookmark old = FindId(db, tx, id) ?? throw new BookmarkException(ResultCode.TargetUnavailable);
            if (OfficeLocation.IsWebTarget(old.Target)) throw new BookmarkException(ResultCode.InvalidRequest);
            if (old.Target.Kind != validatedTarget.Kind || !string.Equals(old.Target.SheetName, validatedTarget.SheetName, StringComparison.Ordinal)
                || !string.Equals(old.Target.CellAddress, validatedTarget.CellAddress, StringComparison.Ordinal)
                || old.Target.WordStart != validatedTarget.WordStart || old.Target.SlideId != validatedTarget.SlideId || old.Target.PdfPage != validatedTarget.PdfPage || old.Target.TextOffset != validatedTarget.TextOffset) throw new BookmarkException(ResultCode.InvalidRequest);
            Bookmark? duplicate = FindTarget(db, tx, validatedTarget, normalized);
            if (duplicate != null && duplicate.Id != id) throw new BookmarkException(ResultCode.DuplicateTarget);
            return ExecuteExisting(db, tx, id, "UPDATE bookmarks SET path=$path,normalized_path=$normalized,display_name=$display WHERE id=$id",
                ("$path", validatedTarget.Path), ("$normalized", normalized), ("$display", PathPolicy.DisplayName(validatedTarget)));
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
        using var command = Command(db, tx, "SELECT " + Columns + " FROM bookmarks WHERE kind=$kind AND normalized_path=$path AND ifnull(sheet_name,'')=$sheet AND ifnull(cell_address,'')=$cell AND ifnull(word_start,-1)=$word AND ifnull(slide_id,-1)=$slide AND ifnull(pdf_page,-1)=$pdf AND ifnull(text_offset,-1)=$text AND ifnull(text_selection_end,-1)=$textEnd",
            ("$kind", (int)target.Kind), ("$path", normalized), ("$sheet", target.SheetName ?? ""), ("$cell", target.CellAddress ?? ""), ("$word", target.WordStart ?? -1), ("$slide", target.SlideId ?? -1), ("$pdf", target.PdfPage ?? -1), ("$text", target.TextOffset ?? -1), ("$textEnd", target.TextSelectionEnd ?? -1));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Materialize(reader) : null;
    }

    private static Bookmark Materialize(SqliteDataReader row) => new(
        Guid.Parse(row.GetString(0)),
        ValidatedTarget(new CapturedTarget((TargetKind)row.GetInt32(1), row.GetString(2), StringOrNull(row, 4), StringOrNull(row, 5), row.IsDBNull(12) ? null : row.GetBoolean(12),
            row.IsDBNull(16) ? null : row.GetInt32(16), row.IsDBNull(17) ? null : row.GetInt32(17), row.IsDBNull(18) ? null : row.GetInt32(18), row.IsDBNull(19) ? null : row.GetInt32(19), StringOrNull(row, 20), row.IsDBNull(21) ? null : row.GetInt32(21),
            StringOrNull(row, 22), row.IsDBNull(23) ? null : row.GetInt32(23), StringOrNull(row, 24))),
        row.GetString(3), row.GetString(6), row.GetString(7), DateTimeOffset.Parse(row.GetString(8), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(row.GetString(9), CultureInfo.InvariantCulture), row.GetInt64(10), DateOrNull(row, 11), DateOrNull(row, 13),
        row.IsDBNull(14) ? null : (ResultCode)row.GetInt32(14), DateOrNull(row, 15));

    private static CapturedTarget ValidatedTarget(CapturedTarget target)
    {
        if (target.Kind != TargetKind.NotepadSnapshot) return target;
        try { return NotepadSnapshotPolicy.Validate(target); }
        catch (BookmarkException) { throw new BookmarkException(ResultCode.PersistenceFailed); }
    }

    private static string? StringOrNull(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private static DateTimeOffset? DateOrNull(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : DateTimeOffset.Parse(row.GetString(index), CultureInfo.InvariantCulture);
}
