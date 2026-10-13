namespace WorkBookmark.Core;

public sealed record BookmarkTreeRow(Bookmark Bookmark, int Depth, bool HasChildren);

/// <summary>Projects bookmark relationships without changing capture order or persisted parent links.</summary>
public static class BookmarkTree
{
    /// <summary>Active items in parent-first order, with newest captured siblings first. Unavailable parents become roots.</summary>
    public static IReadOnlyList<BookmarkTreeRow> Flatten(IEnumerable<Bookmark> items, ISet<Guid>? collapsed = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var ordered = items.Where(item => item.DeletedAtUtc is null)
            .OrderByDescending(item => item.CaptureSequence).ThenBy(item => item.Id).DistinctBy(item => item.Id).ToArray();
        var byId = ordered.ToDictionary(item => item.Id);
        var parents = ordered.ToDictionary(item => item.Id,
            item => item.ParentId is Guid parent && parent != item.Id && byId.ContainsKey(parent) ? (Guid?)parent : null);

        // Defensive projection of malformed imported data: break each cycle at a deterministic node.
        // Iterative traversal also allows very deep, legitimate trees without overflowing the stack.
        var complete = new HashSet<Guid>();
        foreach (var item in ordered)
        {
            var path = new HashSet<Guid>();
            Guid? current = item.Id;
            while (current is Guid id && !complete.Contains(id))
            {
                if (!path.Add(id)) { parents[id] = null; break; }
                current = parents[id];
            }
            complete.UnionWith(path);
        }

        var children = new Dictionary<Guid, List<Bookmark>>();
        var roots = new List<Bookmark>();
        foreach (var item in ordered)
        {
            if (parents[item.Id] is not Guid parent) roots.Add(item);
            else
            {
                if (!children.TryGetValue(parent, out var siblings)) children[parent] = siblings = [];
                siblings.Add(item);
            }
        }
        var rows = new List<BookmarkTreeRow>(ordered.Length);
        var pending = new Stack<(Bookmark Bookmark, int Depth)>();
        for (int index = roots.Count - 1; index >= 0; index--) pending.Push((roots[index], 0));
        while (pending.TryPop(out var entry))
        {
            bool hasChildren = children.TryGetValue(entry.Bookmark.Id, out var descendants);
            rows.Add(new BookmarkTreeRow(entry.Bookmark, entry.Depth, hasChildren));
            if (!hasChildren || collapsed?.Contains(entry.Bookmark.Id) == true) continue;
            for (int index = descendants!.Count - 1; index >= 0; index--) pending.Push((descendants[index], entry.Depth + 1));
        }
        return rows;
    }

    /// <summary>Checks a proposed move. Include deleted items when available to check their retained ancestry too.</summary>
    public static bool CanReparent(IEnumerable<Bookmark> items, Guid id, Guid? parentId)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (id == Guid.Empty || parentId == Guid.Empty || id == parentId) return false;
        var byId = items.DistinctBy(item => item.Id).ToDictionary(item => item.Id);
        if (!byId.TryGetValue(id, out var child) || child.DeletedAtUtc is not null) return false;
        if (parentId is null) return true;
        if (!byId.TryGetValue(parentId.Value, out var parent) || parent.DeletedAtUtc is not null) return false;
        var visited = new HashSet<Guid>();
        Guid? current = parentId;
        while (current is Guid ancestor)
        {
            if (ancestor == id || !visited.Add(ancestor)) return false;
            current = byId.TryGetValue(ancestor, out var row) ? row.ParentId : null;
        }
        return true;
    }
}
