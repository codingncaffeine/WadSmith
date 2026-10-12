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

    public void Set(params Element[] elements)
    {
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
