using Microsoft.Data.Sqlite;
using WorkBookmark.Core;

namespace WorkBookmark.Storage;

public sealed partial class SqliteBookmarkRepository
{
    private sealed record RowPatch(string Table, string Key, string Id,
        Dictionary<string, object?>? Before, Dictionary<string, object?>? After);
    private sealed record UndoEntry(IReadOnlyList<RowPatch> Patches);
    private readonly Stack<UndoEntry> _undo = new();
    private static readonly HashSet<string> BookmarkViewColumns = ["folder_expanded", "last_resume_at_utc", "last_resume_result"];
    private static readonly HashSet<string> LayoutViewColumns = ["is_collapsed", "always_on_top"];

    public bool CanUndo { get { lock (_gate) return !_disposed && _undo.Count != 0; } }

    public BookmarkUndoResult Undo() => Read(db =>
    {
        if (_undo.Count == 0) return new BookmarkUndoResult([]);
        var entry = _undo.Peek();
        using var tx = db.BeginTransaction();
        foreach (var patch in entry.Patches)
        {
            var current = ReadRow(db, tx, patch.Table, patch.Key, patch.Id);
            bool matches = patch.After is null ? current is null : current is not null &&
                patch.After.All(pair => current.TryGetValue(pair.Key, out var value) && Equals(value, pair.Value));
            if (!matches)
            {
                _undo.Clear();
                throw new BookmarkException(ResultCode.InvalidRequest);
            }
        }
        // Restore references before deleting identities created by the original command.
        foreach (var patch in entry.Patches.Where(patch => patch.Before is not null))
        {
            var values = patch.Before!;
            string[] names = values.Keys.ToArray();
            var parameters = names.Select((name, index) => ("$v" + index, values[name])).ToArray();
            string sql = patch.After is null
                ? "INSERT INTO " + patch.Table + "(" + string.Join(',', names) + ") VALUES(" + string.Join(',', parameters.Select(parameter => parameter.Item1)) + ")"
                : "UPDATE " + patch.Table + " SET " + string.Join(',', names.Select((name, index) => name + "=$v" + index)) + " WHERE " + patch.Key + "=$key";
            using var command = Command(db, tx, sql, parameters);
            if (patch.After is not null) command.Parameters.AddWithValue("$key", patch.Id);
            if (command.ExecuteNonQuery() != 1) throw new BookmarkException(ResultCode.PersistenceFailed);
        }
        foreach (var patch in entry.Patches.Where(patch => patch.Before is null).OrderBy(patch => patch.Table == "bookmarks"))
        {
            if (patch.Table == "bookmarks")
            {
                using var references = Command(db, tx, "SELECT 1 FROM bookmarks WHERE parent_id=$id LIMIT 1", ("$id", patch.Id));
                if (references.ExecuteScalar() is not null)
                {
                    _undo.Clear();
                    throw new BookmarkException(ResultCode.InvalidRequest);
                }
            }
            using var remove = Command(db, tx, "DELETE FROM " + patch.Table + " WHERE " + patch.Key + "=$id", ("$id", patch.Id));
            if (remove.ExecuteNonQuery() != 1) throw new BookmarkException(ResultCode.PersistenceFailed);
        }
        _beforeCommit?.Invoke("undo");
        tx.Commit();
        _undo.Pop();
        return new BookmarkUndoResult(entry.Patches.Select(patch => Guid.Parse(patch.Id)).Distinct().ToArray());
    });

    private static bool IsUndoable(string operation) => operation is not ("resume" or "folder-expanded" or "sticker-layout");

    // Temporary triggers retain the first pre-command row only for identities actually touched.
    // The durable database receives no history tables/triggers. Inverses below contain only
    // changed columns, so view state and later automatic resume metadata are never overwritten.
    private static void BeginUndoCapture(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (table, key, columns) in new[] { ("bookmarks", "id", Columns), ("sticker_layouts", "bookmark_id", StickerColumns) })
        {
            string audit = "undo_before_" + table;
            string oldValues = string.Join(',', columns.Split(',').Select(column => "OLD." + column));
            using var command = Command(db, tx,
                "CREATE TEMP TABLE " + audit + " AS SELECT 1 AS existed," + columns + " FROM " + table + " WHERE 0;" +
                "CREATE UNIQUE INDEX temp." + audit + "_key ON " + audit + "(" + key + ");" +
                "CREATE TEMP TRIGGER " + audit + "_update BEFORE UPDATE ON main." + table + " BEGIN " +
                "INSERT OR IGNORE INTO " + audit + " VALUES(1," + oldValues + "); END;" +
                "CREATE TEMP TRIGGER " + audit + "_delete BEFORE DELETE ON main." + table + " BEGIN " +
                "INSERT OR IGNORE INTO " + audit + " VALUES(1," + oldValues + "); END;" +
                "CREATE TEMP TRIGGER " + audit + "_insert AFTER INSERT ON main." + table + " BEGIN " +
                "INSERT OR IGNORE INTO " + audit + "(existed," + key + ") VALUES(0,NEW." + key + "); END;");
            command.ExecuteNonQuery();
        }
    }

    private static UndoEntry CompleteUndoCapture(SqliteConnection db, SqliteTransaction tx)
    {
        var patches = new List<RowPatch>();
        foreach (var (table, key, ignored) in new[] { ("bookmarks", "id", BookmarkViewColumns), ("sticker_layouts", "bookmark_id", LayoutViewColumns) })
        {
            var originals = new List<(string Id, Dictionary<string, object?>? Row)>();
            using (var command = Command(db, tx, "SELECT * FROM undo_before_" + table))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    originals.Add((reader.GetString(reader.GetOrdinal(key)), reader.GetBoolean(0) ? ReadValues(reader, 1) : null));
            foreach (var (id, before) in originals)
            {
                var after = ReadRow(db, tx, table, key, id);
                if (before is null && after is null) continue;
                if (before is not null && after is not null)
                {
                    string[] changed = before.Keys.Where(column => column != key && !ignored.Contains(column) && !Equals(before[column], after[column])).ToArray();
                    if (changed.Length == 0) continue;
                    patches.Add(new(table, key, id, changed.ToDictionary(column => column, column => before[column]),
                        changed.ToDictionary(column => column, column => after[column])));
                }
                else
                {
                    // New/deleted identities need their complete writable state for validation
                    // or recreation. Automatically maintained view columns do not veto an undo.
                    var expected = after?.Where(pair => !ignored.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
                    patches.Add(new(table, key, id, before, expected));
                }
            }
        }
        return new(patches);
    }

    private static Dictionary<string, object?>? ReadRow(SqliteConnection db, SqliteTransaction tx, string table, string key, string id)
    {
        using var command = Command(db, tx, "SELECT " + (table == "bookmarks" ? Columns : StickerColumns) + " FROM " + table + " WHERE " + key + "=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadValues(reader) : null;
    }

    private static Dictionary<string, object?> ReadValues(SqliteDataReader reader, int start = 0)
    {
        var result = new Dictionary<string, object?>();
        for (int index = start; index < reader.FieldCount; index++) result[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        return result;
    }
}
