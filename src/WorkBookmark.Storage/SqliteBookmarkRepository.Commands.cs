using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkBookmark.Core;

namespace WorkBookmark.Storage;

public sealed partial class SqliteBookmarkRepository
{
    private static void MigrateSiblingOrder(SqliteConnection db, SqliteTransaction tx)
    {
        var triggers = new List<(string Name, string Sql)>();
        using (var command = Command(db, tx, "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name='bookmarks' AND sql IS NOT NULL"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var trigger in triggers)
        {
            using var remove = Command(db, tx, "DROP TRIGGER \"" + trigger.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            remove.ExecuteNonQuery();
        }
        using (var order = Command(db, tx, @"ALTER TABLE bookmarks ADD COLUMN sibling_order INTEGER;
UPDATE bookmarks SET sibling_order=-capture_sequence;
CREATE INDEX bookmark_sibling_order ON bookmarks(parent_id,sibling_order);")) order.ExecuteNonQuery();
        foreach (var trigger in triggers)
        {
            using var restore = Command(db, tx, trigger.Sql);
            restore.ExecuteNonQuery();
        }
    }

    public void SaveStickerLayouts(IReadOnlyList<StickerLayout> layouts)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        foreach (var layout in layouts) ValidateStickerLayout(layout, ResultCode.InvalidRequest);
        if (layouts.Select(layout => layout.BookmarkId).Distinct().Count() != layouts.Count)
            throw new BookmarkException(ResultCode.InvalidRequest);
        Write("sticker-layouts", (db, tx) =>
        {
            foreach (var layout in layouts) SaveLayout(db, tx, layout);
            return layouts.Count;
        });
    }

    public FolderDropCommit ApplyDrop(BookmarkDropRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Write("folder-drop", (db, tx) => ApplyDrop(db, tx, request));
    }

    public FolderDropCommit SortChildren(Guid parentId, BookmarkSortMode mode)
    {
        if (parentId == Guid.Empty || !Enum.IsDefined(mode)) throw new BookmarkException(ResultCode.InvalidRequest);
        return Write("folder-sort", (db, tx) =>
        {
            RequireFolder(db, tx, parentId);
            var before = Siblings(ReadAll(db, tx), parentId).ToArray();
            var ordered = mode == BookmarkSortMode.Recent
                ? before.OrderByDescending(item => item.CaptureSequence).ToArray()
                : before.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (ordered.Select(item => item.Id).SequenceEqual(before.Select(item => item.Id))) return new FolderDropCommit(null, []);
            SetOrder(db, tx, ordered.Select(item => item.Id).ToArray());
            return new FolderDropCommit(null, ordered.Select(item => FindId(db, tx, item.Id)!).ToArray());
        });
    }

    private FolderDropCommit ApplyDrop(SqliteConnection db, SqliteTransaction tx, BookmarkDropRequest request)
    {
        if (!Enum.IsDefined(request.Kind) || request.BookmarkId == Guid.Empty || request.TargetId == Guid.Empty || request.BeforeId == Guid.Empty)
            throw new BookmarkException(ResultCode.InvalidRequest);
        Bookmark source = RequireActive(db, tx, request.BookmarkId);
        var before = ReadAll(db, tx).ToDictionary(item => item.Id);
        Guid? parent;
        Guid? createdId = null;
        Guid? includeTarget = null;
        if (request.Kind == BookmarkDropKind.Group)
        {
            if (request.TargetId is not Guid targetId || request.BeforeId is not null)
                throw new BookmarkException(ResultCode.InvalidRequest);
            Bookmark target = RequireActive(db, tx, targetId);
            if (target.IsFolder || !BookmarkTree.CanDropOnto(before.Values, source.Id, targetId))
                throw new BookmarkException(ResultCode.InvalidRequest);
            parent = target.ParentId is Guid targetParent && before.TryGetValue(targetParent, out var parentRow) && parentRow.DeletedAtUtc is null
                ? targetParent : null;
            if (parent is Guid parentId) RequireFolder(db, tx, parentId);
            if (parent is not null && request.RootLayout is not null) throw new BookmarkException(ResultCode.InvalidRequest);
            // Remove the source first: replacing the target then preserves its position among
            // the remaining siblings even when the source originally preceded the target.
            var siblings = Siblings(before.Values, parent).Where(item => item.Id != source.Id).Select(item => item.Id).ToList();
            int targetIndex = siblings.IndexOf(targetId);
            if (targetIndex < 0) throw new BookmarkException(ResultCode.InvalidRequest);
            Guid folderId = Guid.NewGuid();
            createdId = folderId;
            InsertFolder(db, tx, folderId, "새 폴더", parent, Timestamp(), NextSequence(db, tx));
            ExecuteExisting(db, tx, folderId, "UPDATE bookmarks SET folder_expanded=1 WHERE id=$id");
            siblings[targetIndex] = folderId;
            SetMembership(db, tx, targetId, folderId, 0);
            SetMembership(db, tx, source.Id, folderId, 1);
            SetOrder(db, tx, siblings);
            if (parent is null)
            {
                if (request.RootLayout is { } layout) SaveLayout(db, tx, layout with { BookmarkId = folderId, ParentId = null });
                else CloneFolderLayout(db, tx, targetId, folderId, null);
            }
        }
        else
        {
            if (request.Kind == BookmarkDropKind.Detach)
            {
                if (request.TargetId is not null || request.BeforeId is not null) throw new BookmarkException(ResultCode.InvalidRequest);
                parent = null;
            }
            else
            {
                parent = request.TargetId;
                if (request.Kind == BookmarkDropKind.MoveInto && (parent is null || request.BeforeId is not null))
                    throw new BookmarkException(ResultCode.InvalidRequest);
            }
            if (parent is Guid parentId)
            {
                RequireFolder(db, tx, parentId);
                includeTarget = parentId;
            }
            if (!BookmarkTree.CanReparent(before.Values, source.Id, parent)) throw new BookmarkException(ResultCode.InvalidRequest);
            if (parent is not null && request.RootLayout is not null) throw new BookmarkException(ResultCode.InvalidRequest);
            if (request.Kind == BookmarkDropKind.Detach && source.ParentId is null)
            {
                if (request.RootLayout is { } existingRootLayout)
                    SaveLayout(db, tx, existingRootLayout with { BookmarkId = source.Id, ParentId = null });
                return new(null, []);
            }
            var siblings = Siblings(before.Values, parent).Where(item => item.Id != source.Id).Select(item => item.Id).ToList();
            if (request.BeforeId == source.Id)
            {
                // The source's own placeholder denotes its original slot, never a new order.
                if (source.ParentId != parent) throw new BookmarkException(ResultCode.InvalidRequest);
                return new(null, []);
            }
            int index = request.BeforeId is Guid beforeId ? siblings.IndexOf(beforeId) : siblings.Count;
            if (index < 0) throw new BookmarkException(ResultCode.InvalidRequest);
            siblings.Insert(index, source.Id);
            var previousSiblings = Siblings(before.Values, parent).Select(item => item.Id).ToArray();
            if (source.ParentId != parent || !siblings.SequenceEqual(previousSiblings))
            {
                SetMembership(db, tx, source.Id, parent, 0);
                SetOrder(db, tx, siblings);
            }
            if (request.RootLayout is { } layout) SaveLayout(db, tx, layout with { BookmarkId = source.Id, ParentId = null });
        }
        var after = ReadAll(db, tx);
        var changed = after.Where(item => !before.TryGetValue(item.Id, out var previous) || item != previous).ToList();
        if (changed.Count > 0 && includeTarget is Guid included && changed.All(item => item.Id != included))
            changed.Add(after.Single(item => item.Id == included));
        return new(createdId is Guid created ? after.Single(item => item.Id == created) : null, changed);
    }

    private static IEnumerable<Bookmark> Siblings(IEnumerable<Bookmark> bookmarks, Guid? parent)
    {
        var active = bookmarks.Where(item => item.DeletedAtUtc is null).ToArray();
        var ids = active.Select(item => item.Id).ToHashSet();
        return active.Where(item => item.ParentId == parent || parent is null && item.ParentId is Guid missing && !ids.Contains(missing))
            .OrderBy(item => item.SiblingOrder ?? -item.CaptureSequence).ThenByDescending(item => item.CaptureSequence).ThenBy(item => item.Id);
    }

    private static void SetMembership(SqliteConnection db, SqliteTransaction tx, Guid id, Guid? parent, long order) =>
        ExecuteExisting(db, tx, id, "UPDATE bookmarks SET parent_id=$parent,sibling_order=$order WHERE id=$id",
            ("$parent", parent?.ToString()), ("$order", order));

    private static void SetOrder(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<Guid> ids)
    {
        for (int index = 0; index < ids.Count; index++)
            ExecuteExisting(db, tx, ids[index], "UPDATE bookmarks SET sibling_order=$order WHERE id=$id", ("$order", index));
    }

    private static long AppendOrder(SqliteConnection db, SqliteTransaction tx, Guid? parent) => BoundaryOrder(db, tx, parent, prepend: false);
    private static long PrependOrder(SqliteConnection db, SqliteTransaction tx, Guid? parent) => BoundaryOrder(db, tx, parent, prepend: true);
    private static long BoundaryOrder(SqliteConnection db, SqliteTransaction tx, Guid? parent, bool prepend)
    {
        using var command = Command(db, tx, "SELECT " + (prepend ? "min" : "max") +
            "(coalesce(sibling_order,-capture_sequence)) FROM bookmarks WHERE parent_id IS $parent AND deleted_at_utc IS NULL",
            ("$parent", parent?.ToString()));
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : checked(Convert.ToInt64(value, CultureInfo.InvariantCulture) + (prepend ? -1 : 1));
    }
}
