using Microsoft.Data.Sqlite;
using WorkBookmark.Core;

namespace WorkBookmark.Storage;

public sealed partial class SqliteBookmarkRepository
{
    private FolderDropCommit ApplySelectionDrop(SqliteConnection db, SqliteTransaction tx, BookmarkDropRequest request)
    {
        var requested = request.SourceIds;
        if (!Enum.IsDefined(request.Kind) || requested.Contains(Guid.Empty) || request.TargetId == Guid.Empty || request.BeforeId == Guid.Empty)
            throw new BookmarkException(ResultCode.InvalidRequest);
        // Validate every identity before removing selected descendants. A stale selection
        // must never quietly turn into a successful partial move.
        foreach (Guid id in requested) RequireActive(db, tx, id);
        var before = ReadAll(db, tx).ToDictionary(item => item.Id);
        var sourceIds = BookmarkTree.SelectionRoots(before.Values, requested);
        var sources = sourceIds.Select(id => before[id]).ToArray();
        var sourceSet = sourceIds.ToHashSet();
        var layouts = request.RootLayouts ?? [];
        foreach (var layout in layouts) ValidateStickerLayout(layout, ResultCode.InvalidRequest);
        if (layouts.Count > 0 && request.Kind != BookmarkDropKind.Detach ||
            layouts.Select(layout => layout.BookmarkId).Distinct().Count() != layouts.Count ||
            layouts.Any(layout => !sourceSet.Contains(layout.BookmarkId) || layout.ParentId is not null) ||
            request.RootLayout is not null && request.Kind != BookmarkDropKind.Group)
            throw new BookmarkException(ResultCode.InvalidRequest);

        Guid? createdId = null;
        Guid? includeTarget = null;
        if (request.Kind == BookmarkDropKind.Group)
        {
            if (request.TargetId is not Guid targetId || request.BeforeId is not null)
                throw new BookmarkException(ResultCode.InvalidRequest);
            Bookmark target = RequireActive(db, tx, targetId);
            if (target.IsFolder || sourceIds.Any(id => !BookmarkTree.CanDropOnto(before.Values, id, targetId)))
                throw new BookmarkException(ResultCode.InvalidRequest);
            Guid? parent = target.ParentId is Guid targetParent && before.TryGetValue(targetParent, out var parentRow) && parentRow.DeletedAtUtc is null
                ? targetParent : null;
            if (parent is Guid parentId) RequireFolder(db, tx, parentId);
            if (parent is not null && request.RootLayout is not null) throw new BookmarkException(ResultCode.InvalidRequest);
            var siblings = Siblings(before.Values, parent).Where(item => !sourceSet.Contains(item.Id)).Select(item => item.Id).ToList();
            int targetIndex = siblings.IndexOf(targetId);
            if (targetIndex < 0) throw new BookmarkException(ResultCode.InvalidRequest);
            Guid folderId = Guid.NewGuid();
            createdId = folderId;
            InsertFolder(db, tx, folderId, "새 폴더", parent, Timestamp(), NextSequence(db, tx));
            ExecuteExisting(db, tx, folderId, "UPDATE bookmarks SET folder_expanded=1 WHERE id=$id");
            siblings[targetIndex] = folderId;
            SetMembership(db, tx, targetId, folderId, 0);
            for (int index = 0; index < sourceIds.Count; index++) SetMembership(db, tx, sourceIds[index], folderId, index + 1);
            SetOrder(db, tx, siblings);
            if (parent is null)
            {
                if (request.RootLayout is { } layout) SaveLayout(db, tx, layout with { BookmarkId = folderId, ParentId = null });
                else CloneFolderLayout(db, tx, targetId, folderId, null);
            }
        }
        else
        {
            Guid? parent = request.TargetId;
            if (request.Kind == BookmarkDropKind.Detach)
            {
                if (request.TargetId is not null || request.BeforeId is not null) throw new BookmarkException(ResultCode.InvalidRequest);
                parent = null;
            }
            else if (request.Kind == BookmarkDropKind.MoveInto && (parent is null || request.BeforeId is not null))
                throw new BookmarkException(ResultCode.InvalidRequest);
            if (parent is Guid parentId)
            {
                RequireFolder(db, tx, parentId);
                includeTarget = parentId;
            }
            if (sourceIds.Any(id => !BookmarkTree.CanReparent(before.Values, id, parent))) throw new BookmarkException(ResultCode.InvalidRequest);
            var previous = Siblings(before.Values, parent).Select(item => item.Id).ToArray();
            var siblings = previous.Where(id => !sourceSet.Contains(id)).ToList();
            int index = siblings.Count;
            if (request.BeforeId is Guid beforeId)
            {
                int originalIndex = Array.IndexOf(previous, beforeId);
                if (originalIndex < 0) throw new BookmarkException(ResultCode.InvalidRequest);
                // Interpret the gap in the original list, then remove all moving rows.
                // Dropping a contiguous selection onto its own gap is a true no-op.
                index = previous.Take(originalIndex).Count(id => !sourceSet.Contains(id));
            }
            siblings.InsertRange(index, sourceIds);
            bool existingRoots = request.Kind == BookmarkDropKind.Detach && sources.All(source => source.ParentId is null);
            if (!existingRoots && (sources.Any(source => source.ParentId != parent) || !siblings.SequenceEqual(previous)))
            {
                foreach (Guid id in sourceIds) SetMembership(db, tx, id, parent, 0);
                SetOrder(db, tx, siblings);
            }
            foreach (var layout in layouts) SaveLayout(db, tx, layout);
        }
        var after = ReadAll(db, tx);
        var changed = after.Where(item => !before.TryGetValue(item.Id, out var previous) || item != previous).ToList();
        if (changed.Count > 0 && includeTarget is Guid included && changed.All(item => item.Id != included))
            changed.Add(after.Single(item => item.Id == included));
        return new(createdId is Guid created ? after.Single(item => item.Id == created) : null, changed);
    }
}
