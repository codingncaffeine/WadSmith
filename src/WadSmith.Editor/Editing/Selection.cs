using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Editor.Editing;

/// <summary>What is selected: any mix of corners, walls, areas and things, in the order they were picked.</summary>
public sealed class Selection
{
    private readonly List<Element> _items = [];
    private readonly HashSet<Element> _set = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<Element> Items => _items;

    public int Count => _items.Count;

    public bool IsEmpty => _items.Count == 0;

    public event Action? Changed;

    /// <summary>Where on the map the click that picked the selection landed (see <see cref="Pick"/>), for tools that work round a point on what is selected; null when it was selected another way.</summary>
    public MapPoint? At { get; private set; }

    public IEnumerable<T> OfType<T>()
        where T : Element => _items.OfType<T>();

    public bool Contains(Element e) => _set.Contains(e);

    /// <summary>The selection before the last one that replaced or cleared it (empty when there was none), for "Bring back the last selection".</summary>
    public IReadOnlyList<Element> Previous => _previous;

    private Element[] _previous = [];

    // A selection about to be replaced or cleared is kept, so it can be brought back (Eureka's "last selection").
    private void Keep()
    {
        if (_items.Count > 0) _previous = [.. _items];
    }

    /// <summary>Selects again what was selected before the last replace or clear, as much of it as is still on the map; the selection it replaces becomes the one kept, so a second time swaps back.</summary>
    public bool BringBack()
    {
        var back = _previous.Where(e => e.OnMap).ToArray();
        if (back.Length == 0) return false;
        Set(back);
        return true;
    }

    public void Set(params Element[] elements)
    {
        if (!elements.SequenceEqual(_items)) Keep();
        At = null;
        _items.Clear();
        _set.Clear();
        foreach (var e in elements)
        {
            if (_set.Add(e)) _items.Add(e);
        }

        Changed?.Invoke();
    }

    /// <summary>Selects one element clicked at <paramref name="at"/> on the map, keeping the point as <see cref="At"/>.</summary>
    public void Pick(Element e, MapPoint at)
    {
        if (_items.Count != 1 || !ReferenceEquals(_items[0], e)) Keep();
        _items.Clear();
        _set.Clear();
        _items.Add(e);
        _set.Add(e);
        At = at;
        Changed?.Invoke();
    }

    public void Toggle(Element e)
    {
        At = null;
        if (_set.Remove(e)) _items.Remove(e);
        else if (_set.Add(e)) _items.Add(e);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        Keep();
        At = null;
        _items.Clear();
        _set.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops anything no longer on the map (after an undo removed it).</summary>
    public void Prune()
    {
        var removed = _items.RemoveAll(e => !e.OnMap);
        if (removed == 0) return;
        At = null;
        _set.RemoveWhere(e => !e.OnMap);
        Changed?.Invoke();
    }
}
