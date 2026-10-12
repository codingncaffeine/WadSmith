using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>What is selected: any mix of corners, walls, areas and things, in the order they were picked.</summary>
public sealed class Selection
{
    private readonly List<Element> _items = [];

    public IReadOnlyList<Element> Items => _items;

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    public event Action? Changed;

    public IEnumerable<T> OfType<T>()
        where T : Element => _items.OfType<T>();

    public bool Contains(Element e) => _items.Contains(e);

    public void Set(params Element[] elements)
    {
        _items.Clear();
        _items.AddRange(elements.Distinct());
        Changed?.Invoke();
    }

    public void Toggle(Element e)
    {
        if (!_items.Remove(e)) _items.Add(e);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops anything no longer on the map (after an undo removed it).</summary>
    public void Prune(EditorMap map)
    {
        var alive = new HashSet<Element>(map.Vertices.Cast<Element>().Concat(map.Lines).Concat(map.Sides).Concat(map.Sectors).Concat(map.Things));
        if (_items.RemoveAll(e => !alive.Contains(e)) > 0) Changed?.Invoke();
    }
}
